namespace Sankore.Modules.Leads.Domain.Events;

using Sankore.Shared.Kernel;

public sealed record OpportunityCreatedDomainEvent(
    Guid OpportunityId, Guid? LeadId, Guid? CustomerId) : DomainEventBase;

/// <summary>The project moved to another step or phase and is still open.</summary>
public sealed record OpportunityStageChangedDomainEvent(
    Guid OpportunityId,
    OpportunityStage PreviousStage,
    OpportunityStage NewStage,
    Guid? PreviousStepId,
    Guid? NewStepId) : DomainEventBase;

/// <summary>The project was won or lost.</summary>
public sealed record OpportunityClosedDomainEvent(
    Guid OpportunityId, OpportunityStage Outcome) : DomainEventBase;
