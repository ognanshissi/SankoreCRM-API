namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One event owed to one subscription (webhooks lot 3): the exact body that is signed and sent,
/// how many times it was tried, and how the partner answered the last time. At most one per
/// (subscription, event) — a redelivered event creates nothing.
///
/// <para>
/// The body is stored in clear: it is built from the catalogue's projection, which carries no
/// personal value and no typed text. It is still blanked when the lead or client it is about is
/// erased (<see cref="Erase"/>), because an identifier of an erased person is theirs too.
/// </para>
/// </summary>
public sealed class WebhookDelivery : AggregateRoot
{
    /// <summary>
    /// The wait after each failed attempt: 1 min, 5 min, 30 min, 2 h, 6 h, 12 h, 24 h — so the
    /// first try plus seven, spread over ~45 hours. Long enough to ride out a partner's weekend
    /// deployment, short enough that a dead address stops costing a worker within two days.
    /// </summary>
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24),
    ];

    public static int MaxAttempts => RetryDelays.Length + 1;

    /// <summary>How long a claimed delivery is held before another run may take it.</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private WebhookDelivery() { }

    public Guid Id { get; private set; }
    public Guid SubscriptionId { get; private set; }

    /// <summary>The integration event's id — the envelope <c>id</c>, the partner's idempotency key.</summary>
    public Guid EventId { get; private set; }

    public string EventCode { get; private set; } = default!;

    /// <summary>The lead and the client the event is about, when it names them — what an erasure looks for.</summary>
    public Guid? LeadId { get; private set; }
    public Guid? ClientId { get; private set; }

    public string Body { get; private set; } = default!;
    public WebhookDeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public int? LastStatusCode { get; private set; }
    public string? LastFailureCode { get; private set; }
    public int? LastDurationMs { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>xmin: the scheduled run and the safety sweep must not both send it.</summary>
    public uint Version { get; private set; }

    public static WebhookDelivery Create(
        Guid tenantId, Guid subscriptionId, Guid eventId, string eventCode, Guid? leadId, Guid? clientId,
        string body, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            EventId = eventId,
            EventCode = eventCode,
            LeadId = leadId,
            ClientId = clientId,
            Body = body,
            Status = WebhookDeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };

    /// <summary>
    /// Takes the delivery for one attempt: only a pending one that is due. Pushes its next attempt
    /// out by <see cref="ClaimLease"/>, so a second run reading it meanwhile finds it not due — and
    /// the row version catches the two that read it at the same instant.
    /// </summary>
    public bool TryClaim(DateTimeOffset now)
    {
        if (Status != WebhookDeliveryStatus.Pending || NextAttemptAt is null || NextAttemptAt > now)
            return false;

        NextAttemptAt = now + ClaimLease;
        return true;
    }

    /// <returns>True when another attempt is scheduled (<see cref="NextAttemptAt"/>).</returns>
    public bool RecordAttempt(int? statusCode, bool succeeded, int durationMs, string? failureCode, DateTimeOffset now)
    {
        Attempts++;
        LastAttemptAt = now;
        LastStatusCode = statusCode;
        LastDurationMs = durationMs;
        LastFailureCode = succeeded ? null : failureCode;

        if (succeeded)
        {
            Status = WebhookDeliveryStatus.Succeeded;
            DeliveredAt = now;
            NextAttemptAt = null;
            return false;
        }

        if (Attempts >= MaxAttempts)
        {
            Status = WebhookDeliveryStatus.Failed;
            NextAttemptAt = null;
            return false;
        }

        NextAttemptAt = now + RetryDelays[Attempts - 1];
        return true;
    }

    /// <summary>Gives up without sending — the subscription was switched off or deleted.</summary>
    public void Cancel(string reason)
    {
        if (Status != WebhookDeliveryStatus.Pending) return;
        Status = WebhookDeliveryStatus.Cancelled;
        LastFailureCode = reason;
        NextAttemptAt = null;
    }

    /// <summary>« Renvoyer »: the same body, the same event id, a fresh round of attempts from now.</summary>
    public Result Replay(DateTimeOffset now)
    {
        if (Body.Length == 0) return Result.Fail(WebhookErrors.DeliveryErased);

        Status = WebhookDeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        DeliveredAt = null;
        return Result.Ok();
    }

    /// <summary>The person it is about was erased: nothing more goes out, and the body goes.</summary>
    public void Erase()
    {
        Cancel(ErasedReason);
        Body = string.Empty;
    }

    public const string ErasedReason = "SUBJECT_ERASED";
}

public enum WebhookDeliveryStatus
{
    Pending,
    Succeeded,
    Failed,
    Cancelled,
}
