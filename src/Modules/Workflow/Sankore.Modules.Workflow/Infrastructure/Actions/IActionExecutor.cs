using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions;

/// <summary>
/// Typed handler for a single <see cref="ActionType"/>.
/// Each implementation handles exactly one action type and is registered in DI.
/// </summary>
internal interface IActionExecutor
{
    ActionType ActionType { get; }
    Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct);
}
