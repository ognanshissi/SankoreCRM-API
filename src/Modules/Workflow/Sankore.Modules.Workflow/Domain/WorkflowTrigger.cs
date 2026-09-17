namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Maps a domain event (or schedule) to an automatic workflow instance start.
/// When a <see cref="Sankore.Shared.Infrastructure.Workflow.WorkflowTriggerSignal"/> arrives
/// whose EntityType + EventName match a registered and active trigger,
/// the Workflow module automatically starts a new instance on the linked template.
/// </summary>
public sealed class WorkflowTrigger
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TemplateId { get; private set; }
    public TriggerType TriggerType { get; private set; }

    /// <summary>
    /// For <see cref="TriggerType.EntityEvent"/> and <see cref="TriggerType.ExternalEvent"/>:
    /// the event name to match (e.g. <c>"LEAD_CAPTURED"</c>).
    /// For <see cref="TriggerType.Schedule"/>: the cron expression.
    /// </summary>
    public string EventName { get; private set; } = string.Empty;

    /// <summary>
    /// Optional JSON-serialised condition tree evaluated against the signal context.
    /// When null the trigger fires unconditionally on every matching signal.
    /// </summary>
    public string? ConditionJson { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private WorkflowTrigger() { }

    public static WorkflowTrigger Create(
        Guid tenantId,
        Guid templateId,
        TriggerType triggerType,
        string eventName,
        string? conditionJson = null) =>
        new()
        {
            Id            = Guid.NewGuid(),
            TenantId      = tenantId,
            TemplateId    = templateId,
            TriggerType   = triggerType,
            EventName     = eventName,
            ConditionJson = conditionJson,
            IsActive      = true,
            CreatedAt     = DateTimeOffset.UtcNow
        };

    public void Deactivate() => IsActive = false;
}
