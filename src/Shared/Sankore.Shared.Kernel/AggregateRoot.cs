namespace Sankore.Shared.Kernel;

/// <summary>
/// Base class for every aggregate root in every module.
/// Provides domain event tracking so that infrastructure (EF Core SaveChanges
/// interceptor) can dispatch events after a successful commit, without any
/// module needing to know about the messaging infrastructure.
/// </summary>
public abstract class AggregateRoot: ITenant
{
    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>Events raised by this aggregate since it was loaded / created.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
    public Guid TenantId { get; private set; }
}
