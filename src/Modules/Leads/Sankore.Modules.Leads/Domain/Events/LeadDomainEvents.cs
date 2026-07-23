namespace Sankore.Modules.Leads.Domain.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// In-process domain events raised by the Lead aggregate itself. These are
/// distinct from the integration events published by the DispatchLead slice:
/// domain events model "what changed in this aggregate" and may be consumed
/// synchronously within the same module (e.g. to update projections),
/// whereas integration events are the asynchronous, cross-module contract.
/// </summary>
public sealed record LeadCapturedDomainEvent(Guid LeadId) : DomainEventBase;

public sealed record LeadQualifiedDomainEvent(
    Guid LeadId, LeadStatus NewStatus, int Score) : DomainEventBase;

public sealed record LeadAssignedDomainEvent(
    Guid LeadId, Guid AssignmentId, Guid AgentId) : DomainEventBase;
