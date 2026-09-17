using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// A human work-item created by the <see cref="ActionType.CreateTask"/> action.
/// An agent must complete (or cancel) the task; completing it fires
/// <see cref="EventCodes.TaskCompleted"/> on the parent <see cref="WorkflowInstance"/>,
/// which triggers the matching transition in the state machine.
/// </summary>
public sealed class WorkflowTask : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid InstanceId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid? AssignedToUserId { get; private set; }
    public string? AssignedRoleCode { get; private set; }
    public TaskPriority Priority { get; private set; }
    public DateTimeOffset? DueAt { get; private set; }
    public WorkflowTaskStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? CompletionComment { get; private set; }

    private WorkflowTask() { }

    public static WorkflowTask Create(
        Guid tenantId,
        Guid instanceId,
        string title,
        string? description = null,
        Guid? assignedToUserId = null,
        string? assignedRoleCode = null,
        TaskPriority priority = TaskPriority.Normal,
        int? dueDays = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("Task title is required.");

        return new WorkflowTask
        {
            Id               = Guid.NewGuid(),
            TenantId         = tenantId,
            InstanceId       = instanceId,
            Title            = title.Trim(),
            Description      = description?.Trim(),
            AssignedToUserId = assignedToUserId,
            AssignedRoleCode = assignedRoleCode,
            Priority         = priority,
            DueAt            = dueDays.HasValue ? DateTimeOffset.UtcNow.AddDays(dueDays.Value) : null,
            Status           = WorkflowTaskStatus.Pending,
            CreatedAt        = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Marks the task as completed. The calling handler must then fire
    /// <see cref="EventCodes.TaskCompleted"/> on the parent instance.
    /// </summary>
    public void Complete(Guid byUserId, string? comment = null)
    {
        if (Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
            throw new DomainException("Task is already finished.");

        Status            = WorkflowTaskStatus.Completed;
        CompletedAt       = DateTimeOffset.UtcNow;
        CompletionComment = comment;
    }

    public void Cancel()
    {
        if (Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
            throw new DomainException("Task is already finished.");

        Status      = WorkflowTaskStatus.Cancelled;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void Assign(Guid userId)
    {
        if (Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
            throw new DomainException("Cannot assign a finished task.");

        AssignedToUserId = userId;
        Status           = WorkflowTaskStatus.InProgress;
    }
}
