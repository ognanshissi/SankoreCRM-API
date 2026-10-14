namespace Sankore.Modules.Notifications.Domain;

/// <summary>
/// A text message queued for delivery.
///
/// <para>
/// <b>Its own table rather than a <c>Channel</c> column on <see cref="EmailOutboxMessage"/>.</b>
/// One table would mean every email-specific column nullable, two background processors competing
/// for the same hot rows, and a query on each side that has to remember to filter by channel — a
/// filter whose absence sends an SMS through the email sender and reports it delivered.
/// </para>
///
/// <para>
/// What it carries that the email message does not is <see cref="ProviderMessageId"/>: a text
/// message's delivery is reported back by the provider minutes later, against the id it assigned,
/// and without storing that id a status callback has nothing to correlate to.
/// </para>
/// </summary>
public sealed class SmsOutboxMessage
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Source module — for observability, like its email twin.</summary>
    public string Module { get; private set; } = default!;

    public string TemplateKey { get; private set; } = default!;
    public string Locale { get; private set; } = default!;

    /// <summary>
    /// The destination in E.164 (<c>+225…</c>).
    ///
    /// <para>
    /// <b>Stored in clear, and that is a decision worth stating.</b> M01 keeps a client's
    /// telephone AES-256-GCM encrypted, and this row holds it readable — because the processor
    /// has to hand it to a provider minutes later, in another process, and a queue it cannot read
    /// is a queue it cannot send. The mitigation is lifetime, not secrecy: see
    /// <see cref="Redact"/>, which the processor calls on the terminal states so the number
    /// survives only as long as the delivery needs it. The same trade the email outbox makes with
    /// <c>RecipientEmail</c>.
    /// </para>
    /// </summary>
    public string RecipientPhone { get; private set; } = default!;

    /// <summary>JSON-serialised template data.</summary>
    public string TemplateDataJson { get; private set; } = default!;

    /// <summary>Caller-supplied key — duplicates are dropped before queuing.</summary>
    public string IdempotencyKey { get; private set; } = default!;

    public SmsOutboxStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>
    /// The id the provider assigned — Twilio's <c>SM…</c> SID, Brevo's message reference. What a
    /// status callback is correlated on, and the only reason a delivery webhook can say anything
    /// about a specific message.
    /// </summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>
    /// What the provider last said about delivery, as IT names it — <c>queued</c>, <c>sent</c>,
    /// <c>delivered</c>, <c>undelivered</c>, <c>failed</c>. Verbatim and not parsed into an enum:
    /// the vocabularies differ between providers and a value this module does not recognise must
    /// still be readable by whoever is debugging a non-delivery.
    /// </summary>
    public string? ProviderStatus { get; private set; }

    /// <summary>
    /// How many segments the rendered body took. Stored because it is what the tenant is billed
    /// for, and because a template that quietly grew past 160 characters doubles a cost nobody
    /// notices until an invoice arrives.
    /// </summary>
    public int SegmentCount { get; private set; }

    private SmsOutboxMessage() { }

    public static SmsOutboxMessage Create(
        Guid tenantId,
        string module,
        string templateKey,
        string locale,
        string recipientPhone,
        string templateDataJson,
        string idempotencyKey) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Module = module,
            TemplateKey = templateKey,
            Locale = locale,
            RecipientPhone = recipientPhone,
            TemplateDataJson = templateDataJson,
            IdempotencyKey = idempotencyKey,
            Status = SmsOutboxStatus.Pending,
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    public void MarkSending()
    {
        Status = SmsOutboxStatus.Sending;
        LastAttemptAt = DateTimeOffset.UtcNow;
    }

    public void MarkSent(string? providerMessageId, int segmentCount)
    {
        Status = SmsOutboxStatus.Sent;
        SentAt = DateTimeOffset.UtcNow;
        ProviderMessageId = providerMessageId;
        SegmentCount = segmentCount;
    }

    public void MarkFailed(string error, int maxAttempts)
    {
        AttemptCount++;
        LastAttemptAt = DateTimeOffset.UtcNow;
        LastError = error;
        Status = AttemptCount >= maxAttempts
            ? SmsOutboxStatus.DeadLettered
            : SmsOutboxStatus.Failed;
    }

    /// <summary>
    /// Refused by the tenant's allowance, or sent on a channel that is not configured. Straight to
    /// dead-letter in both cases: neither resolves before somebody changes a setting, and burning
    /// the retry budget on them replaces the real reason with "max attempts reached".
    /// </summary>
    public void MarkRefused(string reason)
    {
        AttemptCount++;
        LastAttemptAt = DateTimeOffset.UtcNow;
        LastError = reason;
        Status = SmsOutboxStatus.DeadLettered;
    }

    /// <summary>
    /// What the provider's callback reported, minutes or hours after the send.
    ///
    /// <para>
    /// It does NOT move <see cref="Status"/> back from <c>Sent</c>: this row records what THIS
    /// platform did, and it did hand the message over. A provider reporting <c>undelivered</c> is
    /// a fact about the handset, which belongs beside it rather than on top of it — and rewriting
    /// the send status would make the outbox processor pick the message up again and send it
    /// twice.
    /// </para>
    /// </summary>
    public void RecordProviderStatus(string providerStatus, string? error)
    {
        ProviderStatus = providerStatus;

        // Only when the provider explains a failure, and never overwriting our own error with a
        // null: a callback that says "delivered" must not erase the reason an earlier attempt
        // failed.
        if (!string.IsNullOrWhiteSpace(error))
            LastError = error.Length > 1000 ? error[..1000] : error;
    }

    /// <summary>
    /// Replaces the destination with its masked form, once the message can no longer be sent.
    ///
    /// <para>
    /// The number is in this table because the processor needs it; it has no reason to stay there
    /// afterwards. Masked rather than nulled so a support request about "the message to
    /// +225…3456" is still answerable, and so a delivery webhook arriving after the fact still
    /// finds a row shaped the way it expects.
    /// </para>
    /// </summary>
    public void Redact(string maskedPhone)
    {
        if (!string.IsNullOrWhiteSpace(maskedPhone))
            RecipientPhone = maskedPhone;
    }

    public void ResetForRetry()
    {
        Status = SmsOutboxStatus.Pending;
        AttemptCount = 0;
        LastError = null;
    }
}
