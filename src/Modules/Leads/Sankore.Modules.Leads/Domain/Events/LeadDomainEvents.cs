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

/// <summary>
/// Raised when a lead is attached to an existing M01 client (BNF-B-22). Carries ids only — the
/// client's identity, household, PEP and FATCA stay on the client record and are read through
/// M01's contract, never copied into the leads schema (BNF-B-23).
/// </summary>
public sealed record LeadLinkedToCustomerDomainEvent(
    Guid LeadId, Guid CustomerId, bool IsAutomatic) : DomainEventBase;

/// <summary>Raised when an agent detaches a lead from a client it was wrongly attached to.</summary>
public sealed record LeadDetachedFromCustomerDomainEvent(
    Guid LeadId, Guid PreviousCustomerId) : DomainEventBase;

/// <summary>Raised when a spouse or a child is added to, or removed from, a lead's household.</summary>
public sealed record LeadHouseholdChangedDomainEvent(
    Guid LeadId, Guid MemberId, string MemberType, bool Added) : DomainEventBase;

/// <summary>
/// Raised when a lead's PEP exposure flips (BNF-B-05). Consumed by dispatching, which routes an
/// exposed lead to a cleared agent, and by M08 for the compliance notification.
/// </summary>
public sealed record LeadPepExposureChangedDomainEvent(
    Guid LeadId, bool HasPepExposure) : DomainEventBase;

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
