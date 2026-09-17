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

    /// <summary>
    /// Id of the <see cref="WorkflowStepDefinition"/> that represents the current state in the
    /// state machine. Null before start or when the workflow has reached a terminal state.
    /// </summary>
    public Guid? CurrentStateId { get; private set; }

    public Guid StartedByUserId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// Snapshot of <see cref="WorkflowTemplate.Version"/> at the time this instance was started.
    /// Allows running instances to be unaffected when the template is re-versioned.
    /// </summary>
    public int TemplateVersion { get; private set; } = 1;

    /// <summary>
    /// JSON snapshot of the entity context captured at start time.
    /// Used by rule evaluation when advancing steps.
    /// </summary>
    public string ContextJson { get; private set; } = "{}";

    private readonly List<WorkflowInstanceStep> _steps = [];
    public IReadOnlyCollection<WorkflowInstanceStep> Steps => _steps.AsReadOnly();

    private WorkflowInstance() { }

    private static readonly IReadOnlyDictionary<string, object> _emptyContext =
        new Dictionary<string, object>();

    private readonly List<WorkflowAction> _pendingActions = [];

    /// <summary>
    /// Actions queued by <see cref="Fire"/> after a transition is matched.
    /// Consumed and dispatched by the application handler after SaveChanges.
    /// </summary>
    public IReadOnlyList<WorkflowAction> PendingActions => _pendingActions.AsReadOnly();

    private readonly List<WorkflowAuditEntry> _pendingAuditEntries = [];

    /// <summary>
    /// Immutable audit entries written by <see cref="Fire"/> on every transition.
    /// Handlers must persist these to the database (AddRange before SaveChanges).
    /// </summary>
    public IReadOnlyList<WorkflowAuditEntry> PendingAuditEntries => _pendingAuditEntries.AsReadOnly();

    /// <summary>
    /// Creates a new instance and materialises all step definitions into
    /// <see cref="WorkflowInstanceStep"/> records. Evaluates SkipIf/RequireIf/AutoApproveIf
    /// rules when advancing to the first step.
    /// </summary>
    public static WorkflowInstance Start(
        WorkflowTemplate template,
        Guid entityId,
        Guid startedByUserId,
        string contextJson = "{}",
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null)
    {
        if (!template.IsActive)
            throw new DomainException("Cannot start a workflow from an inactive template.");

        var instance = new WorkflowInstance
        {
            Id               = Guid.NewGuid(),
            TenantId         = template.TenantId,
            TemplateId       = template.Id,
            TemplateVersion  = template.Version,
            EntityType       = template.EntityType,
            EntityId         = entityId,
            Status           = WorkflowStatus.Pending,
            CurrentStepOrder = 0,
            StartedByUserId  = startedByUserId,
            StartedAt        = DateTimeOffset.UtcNow,
            ContextJson      = contextJson
        };

        foreach (var def in template.Steps.OrderBy(s => s.Order))
            instance._steps.Add(WorkflowInstanceStep.Create(template.TenantId, instance.Id, def));

        instance.AdvanceToNextStep(rulesByStepDefId, context, evaluator);
        return instance;
    }

    /// <summary>
    /// Records an approval decision on the current step and advances (or completes) the workflow.
    /// When <paramref name="transitions"/> is supplied the state machine path is taken;
    /// otherwise falls back to the rule-based <see cref="AdvanceToNextStep"/> logic.
    /// </summary>
    public void Approve(
        Guid actedByUserId,
        string? comment = null,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
    {
        _ = CurrentStep() ?? throw new DomainException("No active step found.");

        if (transitions is { Count: > 0 })
            Fire(EventCodes.Approve, actedByUserId, comment, transitions, context, conditionEvaluator, actionsByTransitionId);
        else
        {
            CurrentStep()!.Approve(actedByUserId, comment);
            AdvanceToNextStep(rulesByStepDefId, context, evaluator);
        }
    }

    /// <summary>
    /// Records a rejection and stops the workflow.
    /// When <paramref name="transitions"/> is supplied the state machine path is taken.
    /// </summary>
    public void Reject(
        Guid actedByUserId,
        string? comment = null,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<string, object>? context = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
    {
        _ = CurrentStep() ?? throw new DomainException("No active step found.");

        if (transitions is { Count: > 0 })
            Fire(EventCodes.Reject, actedByUserId, comment, transitions, context, conditionEvaluator, actionsByTransitionId);
        else
        {
            CurrentStep()!.Reject(actedByUserId, comment);
            Status = WorkflowStatus.Rejected;
            CompletedAt = DateTimeOffset.UtcNow;
            RaiseDomainEvent(new WorkflowRejectedEvent(Id, TenantId, EntityType, EntityId));
        }
    }

    /// <summary>
    /// Drives the state machine by firing <paramref name="eventCode"/> from the current state.
    /// Selects the lowest-priority eligible transition — the first one (by Priority) whose
    /// <see cref="WorkflowTransition.ConditionJson"/> evaluates to true, or whose condition is null.
    /// </summary>
    private void Fire(
        string eventCode,
        Guid actedByUserId,
        string? comment,
        IReadOnlyCollection<WorkflowTransition> transitions,
        IReadOnlyDictionary<string, object>? context = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
    {
        var current = CurrentStep()!;
        var ctx     = context ?? _emptyContext;

        var transition = transitions
            .Where(t => t.FromStateId == current.StepDefinitionId && t.EventCode == eventCode)
            .OrderBy(t => t.Priority)
            .FirstOrDefault(t =>
                t.ConditionJson is null ||
                conditionEvaluator?.Evaluate(t.ConditionJson, ctx) == true)
            ?? throw new DomainException(
                $"No eligible transition for event '{eventCode}' from the current state. " +
                "Ensure at least one transition has a matching condition or no condition.");

        switch (eventCode)
        {
            case EventCodes.Approve: current.Approve(actedByUserId, comment); break;
            case EventCodes.Reject:  current.Reject(actedByUserId, comment);  break;
            case EventCodes.Timeout: current.MarkTimedOut();                  break;
        }

        if (transition.ToStateId.HasValue)
        {
            var nextStep = _steps.FirstOrDefault(s => s.StepDefinitionId == transition.ToStateId.Value)
                ?? throw new DomainException("Target state not found in instance steps.");
            nextStep.StartReview();
            CurrentStepOrder = nextStep.Order;
            CurrentStateId   = transition.ToStateId.Value;
            Status           = WorkflowStatus.InProgress;
        }
        else
        {
            Status         = transition.ToTerminalStatus ?? WorkflowStatus.Completed;
            CompletedAt    = DateTimeOffset.UtcNow;
            CurrentStateId = null;

            if (Status == WorkflowStatus.Completed)
                RaiseDomainEvent(new WorkflowCompletedEvent(Id, TenantId, EntityType, EntityId));
            else if (Status == WorkflowStatus.Rejected)
                RaiseDomainEvent(new WorkflowRejectedEvent(Id, TenantId, EntityType, EntityId));
        }

        // Queue actions for the matched transition so the handler can dispatch them after SaveChanges.
        if (actionsByTransitionId?.TryGetValue(transition.Id, out var actions) == true)
            _pendingActions.AddRange(actions.OrderBy(a => a.ExecutionOrder));

        // Append an immutable audit record for this transition.
        _pendingAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:        TenantId,
            instanceId:      Id,
            fromStateId:     current.StepDefinitionId,
            toStateId:       transition.ToStateId,
            eventCode:       eventCode,
            actedByUserId:   actedByUserId,
            comment:         comment,
            contextSnapshot: ContextJson));
    }

    /// <summary>
    /// Fires an arbitrary event code through the state machine.
    /// Used for events beyond APPROVE/REJECT — e.g. <see cref="EventCodes.TaskCompleted"/>.
    /// </summary>
    public void AdvanceByEvent(
        string eventCode,
        Guid actedByUserId,
        string? comment = null,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<string, object>? context = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
    {
        _ = CurrentStep() ?? throw new DomainException("No active step found.");

        if (transitions is { Count: > 0 })
            Fire(eventCode, actedByUserId, comment, transitions, context, conditionEvaluator, actionsByTransitionId);
        else
            throw new DomainException($"No transitions provided to fire event '{eventCode}'.");
    }

    /// <summary>
    /// Called by the SLA checker job when the current step's DueAt has passed.
    /// Marks the step as timed out and stops the workflow.
    /// </summary>
    public void TimeoutCurrentStep(Guid? systemUserId = null)
    {
        var current = CurrentStep()
            ?? throw new DomainException("No active step to time out.");

        current.MarkTimedOut();
        Status = WorkflowStatus.TimedOut;
        CompletedAt = DateTimeOffset.UtcNow;

        _pendingAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:      TenantId,
            instanceId:    Id,
            fromStateId:   current.StepDefinitionId,
            toStateId:     null,
            eventCode:     EventCodes.Timeout,
            actedByUserId: systemUserId ?? Guid.Empty,
            contextSnapshot: ContextJson));
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

    private void AdvanceToNextStep(
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId,
        IReadOnlyDictionary<string, object>? context,
        IRuleEvaluator? evaluator)
    {
        var ctx = context ?? _emptyContext;

        foreach (var step in _steps.Where(s => s.Status == StepStatus.Pending).OrderBy(s => s.Order))
        {
            if (evaluator is not null &&
                rulesByStepDefId is not null &&
                rulesByStepDefId.TryGetValue(step.StepDefinitionId, out var rules) &&
                rules.Count > 0)
            {
                var skipIf = rules.Where(r => r.RuleType == RuleType.SkipIf).ToList();
                if (skipIf.Count > 0 && evaluator.Evaluate(skipIf, ctx))
                {
                    step.Skip();
                    continue;
                }

                var requireIf = rules.Where(r => r.RuleType == RuleType.RequireIf).ToList();
                if (requireIf.Count > 0 && !evaluator.Evaluate(requireIf, ctx))
                {
                    step.Skip();
                    continue;
                }

                var autoApprove = rules.Where(r => r.RuleType == RuleType.AutoApproveIf).ToList();
                if (autoApprove.Count > 0 && evaluator.Evaluate(autoApprove, ctx))
                {
                    step.AutoApprove();
                    continue;
                }
            }

            step.StartReview();
            CurrentStepOrder = step.Order;
            CurrentStateId   = step.StepDefinitionId;
            Status           = WorkflowStatus.InProgress;
            return;
        }

        // All remaining steps were skipped/auto-approved, or none were left.
        CurrentStateId = null;
        Status         = WorkflowStatus.Completed;
        CompletedAt    = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new WorkflowCompletedEvent(Id, TenantId, EntityType, EntityId));
    }
}
