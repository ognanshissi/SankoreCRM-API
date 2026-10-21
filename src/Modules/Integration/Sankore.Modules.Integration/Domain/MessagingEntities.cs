namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One webhook delivery received on a messaging connection, kept as it arrived (WA-BE-07).
///
/// <para>
/// <b>The body is stored encrypted and processed later</b>, by a Hangfire job that is handed this
/// row's id and nothing else: the provider is answered in milliseconds, and nothing a customer
/// wrote reaches a job argument, a log line or a queue. The same arrangement as
/// <see cref="TelWebhookEvent"/>, in a table of its own because the two families' bodies have
/// nothing in common and their retention will not either.
/// </para>
///
/// <para>
/// <b>Idempotent at the door on the body's hash</b>, so an identical redelivery does no work; the
/// guarantee that matters — one message recorded once — is M13's unique index on the provider's
/// message id, because one delivery may carry several messages and a provider may batch them
/// differently on a retry.
/// </para>
/// </summary>
public sealed class MsgWebhookEvent : AggregateRoot
{
    private MsgWebhookEvent() { }

    public Guid Id { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>SHA-256 of the body, hex.</summary>
    public string DedupKey { get; private set; } = default!;

    /// <summary>The encrypted body. Read by the processing job and by nothing else.</summary>
    public byte[] PayloadEnc { get; private set; } = default!;

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>What the job concluded — counts and codes, never content.</summary>
    public string? Outcome { get; private set; }

    /// <summary>How many times the job has looked at this row, for an operator reading a stuck one.</summary>
    public int Attempts { get; private set; }

    public static MsgWebhookEvent Receive(
        Guid tenantId, Guid connectionId, string dedupKey, byte[] payloadEnc, DateTimeOffset receivedAt)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");

        if (string.IsNullOrWhiteSpace(dedupKey))
            throw new DomainException("A webhook event must carry a deduplication key.");

        return new MsgWebhookEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            DedupKey = dedupKey.Trim(),
            PayloadEnc = payloadEnc,
            ReceivedAt = receivedAt,
        };
    }

    public void RecordAttempt() => Attempts++;

    public void MarkProcessed(string outcome, DateTimeOffset at)
    {
        ProcessedAt = at;
        Outcome = outcome.Length > 200 ? outcome[..200] : outcome;
    }
}
