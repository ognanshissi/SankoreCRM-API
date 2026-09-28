namespace Sankore.Modules.Customers.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Replay protection for this module's MassTransit consumers (US-M01-BE-13).
/// </summary>
public interface IInboxGuard
{
    /// <summary>
    /// Claims an integration event for processing.
    /// Returns <c>true</c> when this is the first time the event is seen (the caller
    /// should go ahead), <c>false</c> when it has already been processed and must be
    /// silently acknowledged.
    /// </summary>
    Task<bool> TryBeginAsync(Guid eventId, Guid tenantId, string eventType, CancellationToken ct);
}

/// <summary>
/// Inbox based on <c>customers.inbox_messages</c>, whose primary key IS the event id.
///
/// The outbox gives at-least-once delivery, so every consumer will eventually see a
/// duplicate — after a broker retry, a redeploy mid-ack, or an operator replaying a
/// dead-letter queue. Without this guard a redelivered ClientCreatedEvent would, for
/// instance, append a second timeline entry or re-run a side effect.
///
/// The check is the insert itself rather than a read-then-write: two concurrent
/// deliveries of the same event both find nothing, and the one that loses the primary
/// key race returns <c>false</c> here instead of processing the event twice.
/// </summary>
internal sealed class InboxGuard(CustomersDbContext db) : IInboxGuard
{
    public async Task<bool> TryBeginAsync(Guid eventId, Guid tenantId, string eventType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var alreadySeen = await db.InboxMessages
            .IgnoreQueryFilters()
            .AnyAsync(m => m.Id == eventId, ct);

        if (alreadySeen)
            return false;

        db.InboxMessages.Add(InboxMessage.For(eventId, tenantId, eventType));

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race against a concurrent delivery of the same event.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation
        };
}
