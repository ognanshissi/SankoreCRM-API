namespace Sankore.Shared.Kernel;

/// <summary>
/// Base class for every aggregate root in every module; holds the domain events it raised.
///
/// <para>
/// <b>Nothing dispatches them generically.</b> A module that wants them outside the aggregate
/// translates them itself: M13's <c>LeadsDbContext</c> turns the Lead and Opportunity events into
/// <c>Leads.PublicApi</c> lifecycle events at SaveChanges, in the same transaction as the outbox;
/// other modules publish integration events explicitly from their handlers. An event raised on an
/// aggregate of any other module and never translated is lost — by design, not by accident.
/// </para>
/// </summary>
public abstract class AggregateRoot: ITenant
{
    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>Events raised by this aggregate since it was loaded / created.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
    public Guid TenantId { get; protected set; }
}
