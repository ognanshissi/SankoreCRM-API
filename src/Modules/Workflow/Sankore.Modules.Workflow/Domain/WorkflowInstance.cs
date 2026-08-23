using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Running instance of a <see cref="WorkflowTemplate"/> for a specific entity.
/// Tracks which step is active and the overall approval status.
/// </summary>
public sealed class WorkflowInstance : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }

    /// <summary>Copied from the template at instance creation time (snapshot).</summary>
    public string EntityType { get; private set; } = string.Empty;

    /// <summary>Primary key of the entity being approved (e.g. a Lead Id).</summary>
    public Guid EntityId { get; private set; }

    public WorkflowStatus Status { get; private set; }

    /// <summary>Order of the step currently active (0 = not started yet).</summary>
    public int CurrentStepOrder { get; private set; }

    public Guid StartedByUserId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private readonly List<WorkflowInstanceStep> _steps = [];
    public IReadOnlyCollection<WorkflowInstanceStep> Steps => _steps.AsReadOnly();

    private WorkflowInstance() { }

    /// <summary>
    /// Creates a new instance and materialises all step definitions into
    /// <see cref="WorkflowInstanceStep"/> records. Advances to step 1 immediately.
    /// </summary>
    public static WorkflowInstance Start(WorkflowTemplate template, Guid startedByUserId)
    {
        if (!template.IsActive)
            throw new DomainException("Cannot start a workflow from an inactive template.");

        var instance = new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            TenantId = template.TenantId,
            TemplateId = template.Id,
            EntityType = template.EntityType,
            EntityId = Guid.Empty,      // set via the overload below
            Status = WorkflowStatus.Pending,
            CurrentStepOrder = 0,
            StartedByUserId = startedByUserId,
            StartedAt = DateTimeOffset.UtcNow
        };

        foreach (var def in template.Steps.OrderBy(s => s.Order))
            instance._steps.Add(WorkflowInstanceStep.Create(template.TenantId, instance.Id, def));

        instance.AdvanceToNextStep();
        return instance;
    }

    /// <summary>Overload that also sets the target EntityId.</summary>
    public static WorkflowInstance Start(WorkflowTemplate template, Guid entityId, Guid startedByUserId)
    {
        var instance = Start(template, startedByUserId);
        instance.EntityId = entityId;
        return instance;
    }

    /// <summary>
    /// Records an approval decision on the current step and advances (or completes)
    /// the workflow.
    /// </summary>
    public void Approve(Guid actedByUserId, string? comment = null)
    {
        var current = CurrentStep()
            ?? throw new DomainException("No active step found.");

        current.Approve(actedByUserId, comment);

        var hasNext = _steps.Any(s => s.Order > CurrentStepOrder && s.Status == StepStatus.Pending);
        if (hasNext)
        {
            AdvanceToNextStep();
        }
        else
        {
            Status = WorkflowStatus.Completed;
            CompletedAt = DateTimeOffset.UtcNow;
            RaiseDomainEvent(new WorkflowCompletedEvent(Id, TenantId, EntityType, EntityId));
        }
    }

    /// <summary>Records a rejection and stops the workflow.</summary>
    public void Reject(Guid actedByUserId, string? comment = null)
    {
        var current = CurrentStep()
            ?? throw new DomainException("No active step found.");

        current.Reject(actedByUserId, comment);
        Status = WorkflowStatus.Rejected;
        CompletedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new WorkflowRejectedEvent(Id, TenantId, EntityType, EntityId));
    }

    /// <summary>Manually cancels the workflow (admin action).</summary>
    public void Cancel()
    {
        if (Status is WorkflowStatus.Completed or WorkflowStatus.Rejected or WorkflowStatus.Cancelled)
            throw new DomainException("Workflow is already finished.");

        Status = WorkflowStatus.Cancelled;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    private WorkflowInstanceStep? CurrentStep() =>
        _steps.FirstOrDefault(s => s.Order == CurrentStepOrder &&
                                   s.Status == StepStatus.AwaitingApproval);

    private void AdvanceToNextStep()
    {
        var next = _steps
            .Where(s => s.Status == StepStatus.Pending)
            .OrderBy(s => s.Order)
            .FirstOrDefault();

        if (next is null) return;

        next.StartReview();
        CurrentStepOrder = next.Order;
        Status = WorkflowStatus.InProgress;
    }
}
