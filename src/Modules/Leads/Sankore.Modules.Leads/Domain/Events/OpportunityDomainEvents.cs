namespace Sankore.Modules.Leads.Domain.Events;

using Sankore.Shared.Kernel;

public sealed record OpportunityCreatedDomainEvent(
    Guid OpportunityId, Guid? LeadId, Guid? CustomerId) : DomainEventBase;
