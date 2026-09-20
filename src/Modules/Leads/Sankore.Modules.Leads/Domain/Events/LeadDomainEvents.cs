namespace Sankore.Modules.Leads.Domain.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// In-process domain events raised by the Lead aggregate itself.
/// </summary>
public sealed record LeadCapturedDomainEvent(Guid LeadId) : DomainEventBase;

public sealed record LeadQualifiedDomainEvent(
    Guid LeadId, LeadStatus NewStatus, int Score) : DomainEventBase;

public sealed record LeadAssignedDomainEvent(
    Guid LeadId, Guid AssignmentId, Guid AgentId) : DomainEventBase;

public sealed record LeadStatusChangedDomainEvent(
    Guid LeadId, LeadStatus Previous, LeadStatus New) : DomainEventBase;

public sealed record LeadOwnerChangedDomainEvent(
    Guid LeadId, Guid? PreviousOwnerId, Guid NewOwnerId) : DomainEventBase;

public sealed record LeadPipelineStageChangedDomainEvent(
    Guid LeadId, PipelineStage Previous, PipelineStage New) : DomainEventBase;

public sealed record LeadConvertedDomainEvent(
    Guid LeadId, Guid CustomerId) : DomainEventBase;

public sealed record LeadMergedDomainEvent(
    Guid SourceLeadId, Guid TargetLeadId, Guid MergedBy) : DomainEventBase;

public sealed record DuplicateDismissedDomainEvent(
    Guid LeadId, Guid CandidateLeadId, Guid DismissedBy) : DomainEventBase;

public sealed record ConsentRecordedDomainEvent(
    Guid LeadId, Guid ConsentId, string ConsentType) : DomainEventBase;

public sealed record ConsentWithdrawnDomainEvent(
    Guid LeadId, Guid ConsentId, string ConsentType) : DomainEventBase;

public sealed record SlaEscalatedDomainEvent(
    Guid LeadId, Guid AssignmentId, Guid AgentId, int EscalationDepth) : DomainEventBase;
