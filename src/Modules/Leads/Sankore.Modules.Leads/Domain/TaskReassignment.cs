namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Immutable audit record for every task reassignment (US-M13-083).
/// ActorId == null means SYSTEM (e.g. SLA breach auto-trigger).
/// ActorId != null identifies the manager who performed the manual reassignment.
/// </summary>
public sealed class TaskReassignment : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid? PreviousAgentId { get; private set; }
    public Guid NewAgentId { get; private set; }
    public string Reason { get; private set; } = default!;

    /// <summary>Null = SYSTEM-initiated (e.g. SLA breach); non-null = manager actor.</summary>
    public Guid? ActorId { get; private set; }

    public DateTimeOffset ReassignedAt { get; private set; }

    /// <summary>True when the SLA deadline was recalculated as part of this reassignment.</summary>
    public bool SlaExtended { get; private set; }
    public DateTimeOffset? NewSlaDeadline { get; private set; }

    private TaskReassignment() { }

    public static TaskReassignment Create(
        Guid tenantId,
        Guid taskId,
        Guid? previousAgentId,
        Guid newAgentId,
        string reason,
        Guid? actorId,
        bool slaExtended = false,
        DateTimeOffset? newSlaDeadline = null)
        => new()
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            TaskId         = taskId,
            PreviousAgentId = previousAgentId,
            NewAgentId     = newAgentId,
            Reason         = reason,
            ActorId        = actorId,
            ReassignedAt   = DateTimeOffset.UtcNow,
            SlaExtended    = slaExtended,
            NewSlaDeadline = newSlaDeadline
        };
}
