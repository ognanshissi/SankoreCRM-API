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

    /// <summary>Set when this instance was started by a <see cref="EventCodes.StartChildWorkflow"/> action on a parent.</summary>
    public Guid? ParentInstanceId { get; private set; }

    /// <summary>
    /// Id of the child instance this workflow is currently suspended waiting for.
    /// Non-null only when <see cref="Status"/> is <see cref="WorkflowStatus.WaitingForChild"/>.
    /// </summary>
    public Guid? WaitingForChildId { get; private set; }

    /// <summary>
    /// What is being validated, as the STARTING module describes it — the engine only knows an
    /// entity type and an id, which tells an approver nothing (« Validation » of what?). A snapshot
    /// taken at submission: the amount the committee was asked about stays readable even if the
    /// project changes afterwards.
    /// </summary>
    public string? SubjectLabel { get; private set; }

    /// <summary>One line of context under <see cref="SubjectLabel"/> (client, product, amount…).</summary>
    public string? SubjectDetail { get; private set; }

    /// <summary>The app path that opens the subject (« /leads/projets/{id} »).</summary>
    public string? SubjectLink { get; private set; }

    public const int MaxSubjectLabelLength = 200;
    public const int MaxSubjectDetailLength = 400;
    public const int MaxSubjectLinkLength = 300;

    private readonly List<WorkflowInstanceStep> _steps = [];
    public IReadOnlyCollection<WorkflowInstanceStep> Steps => _steps.AsReadOnly();

    private WorkflowInstance() { }

    /// <summary>Records what the instance validates — see <see cref="SubjectLabel"/>. Truncated, never refused.</summary>
    public void DescribeSubject(string? label, string? detail, string? link)
    {
        SubjectLabel  = Clip(label, MaxSubjectLabelLength);
        SubjectDetail = Clip(detail, MaxSubjectDetailLength);
        SubjectLink   = Clip(link, MaxSubjectLinkLength);
    }

    private static string? Clip(string? text, int max)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];

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
        IRuleEvaluator? evaluator = null,
        IReadOnlyCollection<int>? requiredStepOrders = null)
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

        // Steps the caller says do not apply to this entity are skipped BEFORE the first advance, so
        // the instance opens on the first rung that really applies and never shows a step awaiting a
        // decision nobody will be asked for.
        //
        // This is for a caller whose ladder is computed per entity — M02's KYC circuit is one or
        // three rungs depending on the file. Rule-based SkipIf/RequireIf below stays the way a
        // template expresses its OWN conditions; the two are independent and a step skipped here is
        // simply never offered to the evaluator.
        if (requiredStepOrders is not null)
        {
            foreach (var step in instance._steps.Where(s => !requiredStepOrders.Contains(s.Order)))
                step.Skip();
        }

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

        var transition = SelectTransition(current, eventCode, transitions, ctx, conditionEvaluator)
            ?? throw new DomainException(
                $"No eligible transition for event '{eventCode}' from the current state. " +
                "Ensure at least one transition has a matching condition or no condition.");

        switch (eventCode)
        {
            case EventCodes.Approve: current.Approve(actedByUserId, comment); break;
            case EventCodes.Reject:  current.Reject(actedByUserId, comment);  break;
            case EventCodes.Timeout: current.MarkTimedOut();                  break;
        }

        Follow(transition, current, eventCode, actedByUserId, comment, actionsByTransitionId);
        RunAutomaticSteps(transitions, ctx, conditionEvaluator, actionsByTransitionId);
    }

    private WorkflowTransition? SelectTransition(
        WorkflowInstanceStep from,
        string eventCode,
        IReadOnlyCollection<WorkflowTransition> transitions,
        IReadOnlyDictionary<string, object> ctx,
        IConditionEvaluator? conditionEvaluator)
        => transitions
            .Where(t => t.FromStateId == from.StepDefinitionId && t.EventCode == eventCode)
            .OrderBy(t => t.Priority)
            .FirstOrDefault(t =>
                t.ConditionJson is null ||
                conditionEvaluator?.Evaluate(t.ConditionJson, ctx) == true);

    /// <summary>
    /// Franchit the automatic steps the state machine has just entered, one after the other. An
    /// <see cref="EventCodes.Auto"/> transition a designer drew wins; otherwise the generated
    /// <see cref="EventCodes.Approve"/> one carries the circuit on, as an approval would have.
    /// </summary>
    private void RunAutomaticSteps(
        IReadOnlyCollection<WorkflowTransition> transitions,
        IReadOnlyDictionary<string, object> ctx,
        IConditionEvaluator? conditionEvaluator,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId)
    {
        while (Status == WorkflowStatus.InProgress && CurrentStep() is { RunsAutomatically: true } step)
        {
            var transition = SelectTransition(step, EventCodes.Auto, transitions, ctx, conditionEvaluator)
                          ?? SelectTransition(step, EventCodes.Approve, transitions, ctx, conditionEvaluator);

            // No way out drawn: the step stays open, where someone can still approve it by hand.
            if (transition is null) return;

            ExecuteAutomatic(step);
            Follow(transition, step, EventCodes.Auto, Guid.Empty, null, actionsByTransitionId);
        }
    }

    private void ExecuteAutomatic(WorkflowInstanceStep step)
    {
        step.ExecuteAutomatically();
        if (step.Kind == StepKind.Notification)
            RaiseDomainEvent(new NotificationStepExecutedEvent(
                Id, TenantId, EntityType, EntityId, step.Id, step.Name, step.ConfigJson));
    }

    /// <summary>
    /// Ends the « Attente » step the instance is on — its delay ran out, or the event it waited for
    /// arrived — and carries the circuit on as an approval would, through an <see cref="EventCodes.Auto"/>
    /// transition if one is drawn, else the generated <see cref="EventCodes.Approve"/>. The reason
    /// goes into the history. False when the instance is not waiting.
    /// </summary>
    public bool ResumeWait(
        string reason,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
        => Status == WorkflowStatus.InProgress && CurrentStep() is { Kind: StepKind.Wait } step
        && PassByEngine(step, EventCodes.WaitResumed, reason, transitions, rulesByStepDefId, context,
            evaluator, conditionEvaluator, actionsByTransitionId);

    /// <summary>
    /// The client's KYC reached the level the « Vérification KYC » step requires: the step passes
    /// and the circuit goes on. False when that step is not the open one.
    /// </summary>
    public bool PassKycCheck(
        Guid instanceStepId,
        string reason,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
        => Status == WorkflowStatus.InProgress
        && CurrentStep() is { Kind: StepKind.KycCheck } step && step.Id == instanceStepId
        && PassByEngine(step, EventCodes.KycValidated, reason, transitions, rulesByStepDefId, context,
            evaluator, conditionEvaluator, actionsByTransitionId);

    /// <summary>
    /// The open step's delay ran out (product owner, 2026-10-29: escalate, never end the instance):
    /// it is handed to <paramref name="assignTo"/> — a supervisor, another holder of its role — or
    /// stays where it was when nobody was found, with a fresh delay either way. The history says which.
    /// </summary>
    public bool EscalateCurrentStep(Guid? assignTo, string comment, DateTimeOffset now)
    {
        if (Status != WorkflowStatus.InProgress || CurrentStep() is not { } step) return false;

        step.Escalate(assignTo, now);
        _pendingAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:        TenantId,
            instanceId:      Id,
            fromStateId:     step.StepDefinitionId,
            toStateId:       step.StepDefinitionId,
            eventCode:       assignTo.HasValue ? EventCodes.Escalated : EventCodes.SlaReminder,
            actedByUserId:   Guid.Empty,
            comment:         comment,
            contextSnapshot: ContextJson));
        return true;
    }

    /// <summary>
    /// The back office answered for the « Action Integration » step's record (the client created at
    /// the core banking, the contract issued by the insurer): the step passes.
    /// </summary>
    public bool PassIntegration(
        Guid instanceStepId,
        string reason,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
        => Status == WorkflowStatus.InProgress
        && CurrentStep() is { Kind: StepKind.IntegrationAction } step && step.Id == instanceStepId
        && PassByEngine(step, EventCodes.IntegrationSynced, reason, transitions, rulesByStepDefId, context,
            evaluator, conditionEvaluator, actionsByTransitionId);

    /// <summary>
    /// Ends an open step nobody decides (a wait, a KYC check) with its reason in the history, and
    /// carries the circuit on as an approval would — an <see cref="EventCodes.Auto"/> transition if
    /// one is drawn, else the generated <see cref="EventCodes.Approve"/>.
    /// </summary>
    private bool PassByEngine(
        WorkflowInstanceStep step,
        string eventCode,
        string reason,
        IReadOnlyCollection<WorkflowTransition>? transitions,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId,
        IReadOnlyDictionary<string, object>? context,
        IRuleEvaluator? evaluator,
        IConditionEvaluator? conditionEvaluator,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId)
    {
        var ctx = context ?? _emptyContext;
        WorkflowTransition? transition = null;
        if (transitions is { Count: > 0 })
        {
            transition = SelectTransition(step, EventCodes.Auto, transitions, ctx, conditionEvaluator)
                      ?? SelectTransition(step, EventCodes.Approve, transitions, ctx, conditionEvaluator);
            // No way out drawn: the step stays open, where someone can still approve it by hand.
            if (transition is null) return false;
        }

        step.CompleteByEngine();
        _pendingAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:        TenantId,
            instanceId:      Id,
            fromStateId:     step.StepDefinitionId,
            toStateId:       step.StepDefinitionId,
            eventCode:       eventCode,
            actedByUserId:   Guid.Empty,
            comment:         reason,
            contextSnapshot: ContextJson));

        if (transition is not null)
        {
            Follow(transition, step, EventCodes.Auto, Guid.Empty, null, actionsByTransitionId);
            RunAutomaticSteps(transitions!, ctx, conditionEvaluator, actionsByTransitionId);
        }
        else
        {
            AdvanceToNextStep(rulesByStepDefId, context, evaluator);
        }
        return true;
    }

    /// <summary>
    /// Settles the « Condition » step the instance is on: the steps of the branch NOT taken are
    /// skipped, the reason goes into the history, and the circuit carries on into the branch taken
    /// (or past both, if that branch is empty). False when that step is not the open one.
    /// </summary>
    public bool ResolveCondition(
        Guid instanceStepId,
        bool outcome,
        string reason,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
    {
        if (Status != WorkflowStatus.InProgress
            || CurrentStep() is not { Kind: StepKind.Condition } step
            || step.Id != instanceStepId)
            return false;

        var ctx = context ?? _emptyContext;
        WorkflowTransition? transition = null;
        if (transitions is { Count: > 0 })
        {
            transition = SelectTransition(step, EventCodes.Auto, transitions, ctx, conditionEvaluator)
                      ?? SelectTransition(step, EventCodes.Approve, transitions, ctx, conditionEvaluator);
            if (transition is null) return false;
        }

        step.CompleteCondition();

        // The branches are the contiguous steps right after the condition that carry one.
        var losing = outcome ? StepBranch.No : StepBranch.Yes;
        foreach (var branchStep in _steps.Where(s => s.Order > step.Order).OrderBy(s => s.Order)
                     .TakeWhile(s => s.Branch is not null)
                     .Where(s => s.Branch == losing && s.Status == StepStatus.Pending))
            branchStep.Skip();

        _pendingAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:        TenantId,
            instanceId:      Id,
            fromStateId:     step.StepDefinitionId,
            toStateId:       step.StepDefinitionId,
            eventCode:       EventCodes.ConditionResolved,
            actedByUserId:   Guid.Empty,
            comment:         reason,
            contextSnapshot: ContextJson));

        if (transition is not null)
        {
            Follow(transition, step, EventCodes.Auto, Guid.Empty, null, actionsByTransitionId);
            RunAutomaticSteps(transitions!, ctx, conditionEvaluator, actionsByTransitionId);
        }
        else
        {
            AdvanceToNextStep(rulesByStepDefId, context, evaluator);
        }
        return true;
    }

    /// <summary>
    /// The CRM task of the « Tâche manuelle » step the instance is on was completed: the step is
    /// approved by whoever did the task, and the circuit goes on. False when that step is not the
    /// open one any more (approved by hand meanwhile, instance cancelled…).
    /// </summary>
    public bool CompleteManualTask(
        Guid instanceStepId,
        Guid actedByUserId,
        string comment,
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
    {
        if (Status != WorkflowStatus.InProgress
            || CurrentStep() is not { Kind: StepKind.ManualTask } step
            || step.Id != instanceStepId)
            return false;

        Approve(actedByUserId, comment, transitions, rulesByStepDefId, context, evaluator,
            conditionEvaluator, actionsByTransitionId);
        return true;
    }

    /// <summary>
    /// Moves an instance past the automatic step it is stuck on — one opened before the engine ran
    /// them, and left waiting for an approval nobody was asked for. True when something moved.
    /// </summary>
    public bool ResumeAutomaticSteps(
        IReadOnlyCollection<WorkflowTransition>? transitions = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>>? rulesByStepDefId = null,
        IReadOnlyDictionary<string, object>? context = null,
        IRuleEvaluator? evaluator = null,
        IConditionEvaluator? conditionEvaluator = null,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId = null)
    {
        if (Status != WorkflowStatus.InProgress || CurrentStep() is not { RunsAutomatically: true } step)
            return false;

        if (transitions is { Count: > 0 })
        {
            RunAutomaticSteps(transitions, context ?? _emptyContext, conditionEvaluator, actionsByTransitionId);
            return step.Status == StepStatus.Executed;
        }

        ExecuteAutomatic(step);
        AdvanceToNextStep(rulesByStepDefId, context, evaluator);
        return true;
    }

    private void Follow(
        WorkflowTransition transition,
        WorkflowInstanceStep current,
        string eventCode,
        Guid actedByUserId,
        string? comment,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>>? actionsByTransitionId)
    {
        var nextStep = transition.ToStateId.HasValue
            ? _steps.FirstOrDefault(s => s.StepDefinitionId == transition.ToStateId.Value)
                ?? throw new DomainException("Target state not found in instance steps.")
            : null;

        // A step skipped by a « Condition » (the branch not taken) is stepped over: the circuit goes
        // to the next step still pending, in order — where the generated transitions lead anyway.
        if (nextStep is { Status: StepStatus.Skipped })
            nextStep = _steps.Where(s => s.Order > nextStep.Order && s.Status == StepStatus.Pending)
                .OrderBy(s => s.Order)
                .FirstOrDefault();

        if (nextStep is not null)
        {
            nextStep.StartReview();
            CurrentStepOrder = nextStep.Order;
            CurrentStateId   = nextStep.StepDefinitionId;
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

    /// <summary>
    /// Called on the child instance immediately after it is created to record its parent.
    /// </summary>
    internal void SetParent(Guid parentInstanceId) =>
        ParentInstanceId = parentInstanceId;

    /// <summary>
    /// Suspends this instance until the specified child workflow completes.
    /// Transitions the status to <see cref="WorkflowStatus.WaitingForChild"/>.
    /// </summary>
    public void PauseForChild(Guid childInstanceId)
    {
        if (Status is WorkflowStatus.Completed or WorkflowStatus.Rejected
                   or WorkflowStatus.Cancelled or WorkflowStatus.TimedOut)
            throw new DomainException("Cannot pause a workflow that has already reached a terminal state.");

        Status           = WorkflowStatus.WaitingForChild;
        WaitingForChildId = childInstanceId;
    }

    /// <summary>
    /// Restores the instance to <see cref="WorkflowStatus.InProgress"/> after the awaited
    /// child completes, so that a <see cref="EventCodes.ChildCompleted"/> transition can fire.
    /// </summary>
    public void ResumeFromChild(Guid childInstanceId)
    {
        if (Status != WorkflowStatus.WaitingForChild)
            throw new DomainException("Workflow is not waiting for a child instance.");
        if (WaitingForChildId != childInstanceId)
            throw new DomainException("This workflow is not waiting for the specified child instance.");

        Status            = WorkflowStatus.InProgress;
        WaitingForChildId = null;
    }

    /// <summary>Manually cancels the workflow (admin action).</summary>
    public void Cancel()
    {
        if (Status is WorkflowStatus.Completed or WorkflowStatus.Rejected
                   or WorkflowStatus.Cancelled or WorkflowStatus.TimedOut)
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

            if (step.RunsAutomatically)
            {
                ExecuteAutomatic(step);
                continue;
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
