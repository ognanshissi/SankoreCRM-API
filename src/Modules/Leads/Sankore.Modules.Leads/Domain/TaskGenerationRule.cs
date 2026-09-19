namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-scoped configuration that maps a business event type to a task
/// to generate automatically (US-M13-080). Rules are evaluatedby the
/// appropriate MassTransit consumer when the event arrives.
/// </summary>
public sealed class TaskGenerationRule : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// Simple event name used to match the consumer to the right rule(s).
    /// Values are defined in <see cref="TaskTriggerEvents"/>.
    /// </summary>
    public string TriggerEventType { get; private set; } = default!;

    public CrmTaskType TaskType { get; private set; }
    public CrmTaskPriority Priority { get; private set; }

    /// <summary>Task title template — supports {LeadId} placeholder.</summary>
    public string TitleTemplate { get; private set; } = default!;
    public string? DescriptionTemplate { get; private set; }

    /// <summary>SLA window measured from the moment the event occurs.</summary>
    public TimeSpan SlaDuration { get; private set; }

    /// <summary>How far in the future the task's due date is set.</summary>
    public TimeSpan DueDuration { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private TaskGenerationRule() { }

    public static TaskGenerationRule Create(
        Guid tenantId,
        string triggerEventType,
        CrmTaskType taskType,
        CrmTaskPriority priority,
        string titleTemplate,
        TimeSpan slaDuration,
        TimeSpan dueDuration,
        string? descriptionTemplate = null)
        => new()
        {
            Id                  = Guid.NewGuid(),
            TenantId            = tenantId,
            TriggerEventType    = triggerEventType,
            TaskType            = taskType,
            Priority            = priority,
            TitleTemplate       = titleTemplate,
            DescriptionTemplate = descriptionTemplate,
            SlaDuration         = slaDuration,
            DueDuration         = dueDuration,
            IsActive            = true,
            CreatedAt           = DateTimeOffset.UtcNow
        };

    public void Update(
        string triggerEventType,
        CrmTaskType taskType,
        CrmTaskPriority priority,
        string titleTemplate,
        TimeSpan slaDuration,
        TimeSpan dueDuration,
        string? descriptionTemplate = null)
    {
        TriggerEventType    = triggerEventType;
        TaskType            = taskType;
        Priority            = priority;
        TitleTemplate       = titleTemplate;
        DescriptionTemplate = descriptionTemplate;
        SlaDuration         = slaDuration;
        DueDuration         = dueDuration;
    }

    public void Activate()   => IsActive = true;
    public void Deactivate() => IsActive = false;
}
