namespace Sankore.Modules.Customers.Features.Lifecycle.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Applies a rejected KYC file (US-M01-BE-13): the client moves to
/// <c>KycRejected</c> and the analyst's motive is stored on the record so an
/// operator can explain the refusal without opening the KYC module.
///
/// No integration event is published: a rejection is a terminal commercial state,
/// nothing downstream has to be woken up, and the transition is fully traced by
/// the status-history line the aggregate appends.
///
/// Runs outside any HTTP request: <c>IgnoreQueryFilters()</c> plus an explicit
/// TenantId predicate from the event payload.
/// </summary>
public sealed class KycRejectedConsumer(
    CustomersDbContext db,
    IInboxGuard inbox,
    ILogger<KycRejectedConsumer> logger)
    : IConsumer<KycRejectedEvent>
{
    public async Task Consume(ConsumeContext<KycRejectedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        // At-least-once delivery: a replay loses on the inbox primary key and exits
        // without a second history line.
        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId, evt.TenantId, nameof(KycRejectedEvent), ct))
            return;

        // SYSTEM identity: the rejection is attributed to the system account.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, LifecycleActors.System, "SYSTEM");

        var client = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == evt.TenantId && c.Id == evt.CustomerEntityId, ct);

        if (client is null)
        {
            // The guard has already committed the inbox row, so this event is
            // acknowledged and will not come back: a missing client means a stale or
            // bogus reference, logged for investigation.
            logger.LogWarning(
                "KycRejectedEvent ignored: no client {ClientId} in tenant {TenantId}.",
                evt.CustomerEntityId, evt.TenantId);
            return;
        }

        var statusBefore = client.Status;

        var transition = client.ApplyKycRejected(evt.Reason, evt.RejectedAt, LifecycleActors.System);
        if (transition.IsFailure)
        {
            logger.LogWarning(
                "KycRejectedEvent rejected by client {ClientId} (tenant {TenantId}): {Error}.",
                client.Id, evt.TenantId, transition.Error);
            return;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "KYC rejected for client {ClientId} (tenant {TenantId}): {From} -> {To}.",
            client.Id, evt.TenantId, statusBefore, client.Status);
    }
}
