namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// A side-effect action attached to a <see cref="WorkflowTransition"/>.
/// After the transition fires and the state changes, all actions on that transition
/// are executed in <see cref="ExecutionOrder"/> by the <see cref="IActionExecutorDispatcher"/>.
/// </summary>
public sealed class WorkflowAction
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }
    public Guid TransitionId { get; private set; }
    public ActionType ActionType { get; private set; }

    /// <summary>Lower value runs first. Ties are resolved by insertion order.</summary>
    public int ExecutionOrder { get; private set; }

    /// <summary>
    /// JSON-serialised action configuration. Shape depends on <see cref="ActionType"/>:
    /// <list type="bullet">
    ///   <item>AssignUser: <c>{ "userId": "..." }</c></item>
    ///   <item>AssignRoundRobin: <c>{ "roleCode": "..." }</c></item>
    ///   <item>SendNotification: <c>{ "title": "...", "body": "...", "recipientRoleCode": "..." }</c></item>
    ///   <item>CreateTask: <c>{ "title": "...", "assignedRoleCode": "...", "dueDays": 3 }</c></item>
    ///   <item>CallWebhook: <c>{ "url": "...", "method": "POST" }</c></item>
    ///   <item>PublishEvent: <c>{ "eventType": "...", "payload": {} }</c></item>
    ///   <item>StartChildWorkflow: <c>{ "templateEntityType": "..." }</c></item>
    /// </list>
    /// </summary>
    public string ConfigJson { get; private set; } = "{}";

    private WorkflowAction() { }

    public static WorkflowAction Create(
        Guid templateId,
        Guid transitionId,
        ActionType actionType,
        string configJson = "{}",
        int executionOrder = 0) =>
        new()
        {
            Id             = Guid.NewGuid(),
            TemplateId     = templateId,
            TransitionId   = transitionId,
            ActionType     = actionType,
            ConfigJson     = configJson,
            ExecutionOrder = executionOrder
        };
}
