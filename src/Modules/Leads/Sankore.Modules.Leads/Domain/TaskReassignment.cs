namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Immutable audit record for every task reassignment (US-M13-083).
/// ActorId == null means SYSTEM (e.g. SLA breach auto-trigger).
/// ActorId != null identifies the manager who performed the manual reassignment.
///
/// <para>
/// Together with the task's own creation these rows ARE the task's attribution history: who held
/// it, from when, and against which deadline. A row whose previous and new agent are the same is
/// an SLA escalation — the agent keeps the task and only its deadline moves.
/// </para>
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

    /// <summary>
    /// The task's deadline before this row. Null on rows written before it was recorded — the
    /// history then shows the change without its starting point rather than inventing one.
    /// </summary>
    public DateTimeOffset? PreviousSlaDeadline { get; private set; }

    private TaskReassignment() { }

    public static TaskReassignment Create(
        Guid tenantId,
        Guid taskId,
        Guid? previousAgentId,
        Guid newAgentId,
        string reason,
        Guid? actorId,
        bool slaExtended = false,
        DateTimeOffset? newSlaDeadline = null,
        DateTimeOffset? previousSlaDeadline = null,
        DateTimeOffset? reassignedAt = null)
        => new()
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            TaskId         = taskId,
            PreviousAgentId = previousAgentId,
            NewAgentId     = newAgentId,
            Reason         = reason,
            ActorId        = actorId,
            ReassignedAt   = reassignedAt ?? DateTimeOffset.UtcNow,
            SlaExtended    = slaExtended,
            NewSlaDeadline = newSlaDeadline,
            PreviousSlaDeadline = previousSlaDeadline
        };
}
