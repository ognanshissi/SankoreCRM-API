namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Idempotency ledger for the module's MassTransit consumers: the primary key IS the event id,
/// so a redelivered message fails to insert and the consumer exits without applying the effect twice.
/// </summary>
public sealed class InboxMessage
{
    /// <summary>The integration event's <c>EventId</c> — deliberately not a fresh Guid.</summary>
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }
    public string EventType { get; private set; } = default!;
    public DateTimeOffset ReceivedAt { get; private set; }

    private InboxMessage() { } // EF Core

    public static InboxMessage For(Guid eventId, Guid tenantId, string eventType)
    {
        if (eventId == Guid.Empty)
            throw new DomainException("Event id is required.", "InboxMessage.EventId.Required");
        if (string.IsNullOrWhiteSpace(eventType))
            throw new DomainException("Event type is required.", "InboxMessage.EventType.Required");

        return new InboxMessage
        {
            Id = eventId,
            TenantId = tenantId,
            EventType = eventType.Trim(),
            ReceivedAt = DateTimeOffset.UtcNow,
        };
    }
}
