namespace Sankore.Modules.Notifications.Domain;

/// <summary>
/// Represents a transactional email queued for delivery.
/// Written atomically with the triggering business operation; picked up by
/// EmailOutboxProcessor for asynchronous dispatch.
/// </summary>
public sealed class EmailOutboxMessage
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Source module (e.g. "Leads", "Administration") — for observability.</summary>
    public string Module { get; private set; } = default!;

    public string TemplateKey { get; private set; } = default!;

    /// <summary>BCP-47 locale used to select the template variant (e.g. "fr", "en").</summary>
    public string Locale { get; private set; } = default!;

    public string RecipientEmail { get; private set; } = default!;
    public string? RecipientName { get; private set; }

    /// <summary>JSON-serialized Dictionary&lt;string,object&gt; injected into the template.</summary>
    public string TemplateDataJson { get; private set; } = default!;

    /// <summary>Caller-supplied key — duplicates are silently dropped before queuing.</summary>
    public string IdempotencyKey { get; private set; } = default!;

    public EmailOutboxStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    private EmailOutboxMessage() { }

    public static EmailOutboxMessage Create(
        Guid tenantId,
        string module,
        string templateKey,
        string locale,
        string recipientEmail,
        string? recipientName,
        string templateDataJson,
        string idempotencyKey) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Module = module,
        TemplateKey = templateKey,
        Locale = locale,
        RecipientEmail = recipientEmail,
        RecipientName = recipientName,
        TemplateDataJson = templateDataJson,
        IdempotencyKey = idempotencyKey,
        Status = EmailOutboxStatus.Pending,
        AttemptCount = 0,
        CreatedAt = DateTimeOffset.UtcNow
    };

    public void MarkSending()
    {
        Status = EmailOutboxStatus.Sending;
        LastAttemptAt = DateTimeOffset.UtcNow;
    }

    public void MarkSent()
    {
        Status = EmailOutboxStatus.Sent;
        SentAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string error, int maxAttempts)
    {
        AttemptCount++;
        LastAttemptAt = DateTimeOffset.UtcNow;
        LastError = error;
        Status = AttemptCount >= maxAttempts
            ? EmailOutboxStatus.DeadLettered
            : EmailOutboxStatus.Failed;
    }

    /// <summary>Resets a DeadLettered message for manual requeue (V1.1).</summary>
    public void ResetForRetry()
    {
        Status = EmailOutboxStatus.Pending;
        AttemptCount = 0;
        LastError = null;
    }
}
