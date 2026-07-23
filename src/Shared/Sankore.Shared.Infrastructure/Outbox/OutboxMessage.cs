namespace Sankore.Shared.Infrastructure.Outbox;

/// <summary>
/// Row persisted in the SAME transaction as the aggregate change that
/// produced it. This is what makes event publishing reliable: either both
/// the domain change and the outbox row commit together, or neither does.
/// A background processor later reads unprocessed rows and publishes them
/// to the broker, marking them processed only after a successful publish
/// (at-least-once delivery — consumers must be idempotent).
///
/// Each module includes this entity in its OWN DbContext / schema
/// (e.g. "leads.outbox_messages", "users.outbox_messages") rather than a
/// single shared table, to preserve the "no cross-module tables" rule.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public string EventType { get; init; } = default!;
    public string PayloadJson { get; init; } = default!;
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage From<TEvent>(TEvent @event, string eventTypeName, string payloadJson)
        => new()
        {
            Id = Guid.NewGuid(),
            EventType = eventTypeName,
            PayloadJson = payloadJson,
            OccurredAt = DateTimeOffset.UtcNow
        };
}
