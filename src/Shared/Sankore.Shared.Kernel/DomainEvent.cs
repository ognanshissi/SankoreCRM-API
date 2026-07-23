namespace Sankore.Shared.Kernel;

/// <summary>
/// A domain event: something that happened inside an aggregate, relevant
/// only within the boundaries of a single module (in-process, same transaction).
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// An integration event: something that happened in a module and that other
/// modules (or external systems, via the Outbox + message broker) may care about.
/// Integration events are the ONLY contract modules use to talk to each other
/// asynchronously. They must be serializable and immutable.
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// Convenience base record for domain events; sets OccurredAt automatically.
/// </summary>
public abstract record DomainEventBase : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Convenience base record for integration events; sets EventId and OccurredAt automatically.
/// </summary>
public abstract record IntegrationEventBase : IIntegrationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}
