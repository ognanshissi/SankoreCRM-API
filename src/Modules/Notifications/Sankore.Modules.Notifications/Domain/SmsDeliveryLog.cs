namespace Sankore.Modules.Notifications.Domain;

/// <summary>
/// One thing that happened to a text message: this platform handed it over, or the provider
/// reported back on it.
///
/// <para>
/// <b>Its own table beside <see cref="EmailDeliveryLog"/>.</b> That one keys on an email address
/// and an email event vocabulary (bounced, complained); this one keys on a telephone number and
/// the providers' own words. One table with both would mean a nullable address, a nullable number
/// and an event enum that is the union of two unrelated sets.
/// </para>
///
/// <para>
/// The number stored here is MASKED. The log is read by operators and exported; the clear value
/// lives in the outbox row only while the message is in flight, and in M01's encrypted column
/// permanently. A delivery history does not need to be a second directory of telephone numbers.
/// </para>
/// </summary>
public sealed class SmsDeliveryLog
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// The queued message, when the event can be correlated to one. Null for a provider callback
    /// naming an id this platform never issued — which is a thing to keep rather than drop: a
    /// webhook nobody can place is either a misconfiguration or somebody else's traffic, and both
    /// are worth seeing.
    /// </summary>
    public Guid? OutboxMessageId { get; private set; }

    public SmsDeliveryEventType EventType { get; private set; }

    /// <summary>The destination, MASKED (<c>+225 •• •• •• 56</c>).</summary>
    public string MaskedPhone { get; private set; } = default!;

    /// <summary>
    /// The provider's own status word, verbatim, when the event came from a callback. Null on the
    /// events this platform records itself.
    /// </summary>
    public string? ProviderStatus { get; private set; }

    /// <summary>The raw payload, for audit. Kept because a non-delivery dispute is settled on it.</summary>
    public string RawPayload { get; private set; } = default!;

    public DateTimeOffset RecordedAt { get; private set; }

    private SmsDeliveryLog() { }

    public static SmsDeliveryLog Record(
        Guid tenantId,
        Guid? outboxMessageId,
        SmsDeliveryEventType eventType,
        string maskedPhone,
        string rawPayload,
        string? providerStatus = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OutboxMessageId = outboxMessageId,
            EventType = eventType,
            MaskedPhone = maskedPhone,
            ProviderStatus = providerStatus,
            RawPayload = rawPayload,
            RecordedAt = DateTimeOffset.UtcNow,
        };
}

/// <summary>
/// What a log row is about. Four values, and the distinction that matters is between the first two:
/// <c>Accepted</c> is what THIS platform did, <c>Delivered</c> is what the handset did, and
/// conflating them is how a platform reports a 100% delivery rate on messages nobody received.
/// </summary>
public enum SmsDeliveryEventType
{
    /// <summary>The provider accepted the message. Recorded by the outbox processor.</summary>
    Accepted,

    /// <summary>The provider's callback says it reached the handset.</summary>
    Delivered,

    /// <summary>The provider's callback says it did not, and usually why.</summary>
    Undelivered,

    /// <summary>Refused before or during the hand-off: quota, no channel, provider error.</summary>
    Rejected
}
