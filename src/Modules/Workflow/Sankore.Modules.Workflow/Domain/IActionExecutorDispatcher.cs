namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Executes all pending <see cref="WorkflowAction"/> records collected by
/// <see cref="WorkflowInstance"/> after a transition fires.
/// Implemented in the infrastructure layer; injected into application handlers.
/// </summary>
public interface IActionExecutorDispatcher
{
    Task ExecuteAllAsync(
        IReadOnlyList<WorkflowAction> actions,
        WorkflowContext context,
        CancellationToken cancellationToken = default);
}
