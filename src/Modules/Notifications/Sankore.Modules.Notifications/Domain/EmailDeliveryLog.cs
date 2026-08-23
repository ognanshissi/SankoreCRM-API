namespace Sankore.Modules.Notifications.Domain;

/// <summary>
/// Records a delivery event (delivered, bounced, complained, rejected)
/// received from the email provider webhook (e.g. AWS SNS).
/// </summary>
public sealed class EmailDeliveryLog
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Foreign key to EmailOutboxMessage. Nullable: provider events may not always correlate.</summary>
    public Guid? OutboxMessageId { get; private set; }

    public EmailDeliveryEventType EventType { get; private set; }
    public string RecipientEmail { get; private set; } = default!;

    /// <summary>Raw JSON payload from the provider webhook — kept for audit.</summary>
    public string RawPayload { get; private set; } = default!;

    public DateTimeOffset RecordedAt { get; private set; }

    private EmailDeliveryLog() { }

    public static EmailDeliveryLog Record(
        Guid tenantId,
        Guid? outboxMessageId,
        EmailDeliveryEventType eventType,
        string recipientEmail,
        string rawPayload) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        OutboxMessageId = outboxMessageId,
        EventType = eventType,
        RecipientEmail = recipientEmail,
        RawPayload = rawPayload,
        RecordedAt = DateTimeOffset.UtcNow
    };
}
