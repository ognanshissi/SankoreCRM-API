namespace Sankore.Modules.Leads.Features.Tasks.ReassignTask;

using Sankore.Shared.Kernel;

/// <summary>
/// Published when a CRM task is reassigned to a new agent (US-M13-083).
/// Consumed by Notifications (alert the new agent) and Analytics (audit trail).
/// ActorId == null signals SYSTEM-initiated reassignment (e.g. SLA breach).
/// </summary>
public sealed record TaskReassignedEvent(
    Guid TaskId,
    Guid TenantId,
    Guid? PreviousAgentId,
    Guid NewAgentId,
    string Reason,
    Guid? ActorId,
    DateTimeOffset ReassignedAt,
    bool SlaExtended,
    DateTimeOffset? NewSlaDeadline) : IntegrationEventBase;
