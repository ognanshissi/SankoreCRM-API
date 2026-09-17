namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Immutable, append-only record of a single state transition fired on a
/// <see cref="WorkflowInstance"/>. Written by <see cref="WorkflowInstance.Fire"/>
/// and by the SLA checker when a step times out. Never updated after insert.
/// </summary>
public sealed class WorkflowAuditEntry
{
    public Guid Id { get; private set; }
    public Guid InstanceId { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Step definition that was active before the transition. Null at workflow start.</summary>
    public Guid? FromStateId { get; private set; }

    /// <summary>Step definition reached after the transition. Null when the transition is terminal.</summary>
    public Guid? ToStateId { get; private set; }

    public string EventCode { get; private set; } = string.Empty;
    public Guid ActedByUserId { get; private set; }
    public string? Comment { get; private set; }

    /// <summary>JSON snapshot of the instance's context variables at the moment of this transition.</summary>
    public string? ContextSnapshot { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    private WorkflowAuditEntry() { }

    public static WorkflowAuditEntry Create(
        Guid tenantId,
        Guid instanceId,
        Guid? fromStateId,
        Guid? toStateId,
        string eventCode,
        Guid actedByUserId,
        string? comment = null,
        string? contextSnapshot = null) =>
        new()
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenantId,
            InstanceId      = instanceId,
            FromStateId     = fromStateId,
            ToStateId       = toStateId,
            EventCode       = eventCode,
            ActedByUserId   = actedByUserId,
            Comment         = comment,
            ContextSnapshot = contextSnapshot,
            OccurredAt      = DateTimeOffset.UtcNow
        };
}
