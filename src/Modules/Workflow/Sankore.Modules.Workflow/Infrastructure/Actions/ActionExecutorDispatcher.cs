using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions;

/// <summary>
/// Routes each <see cref="WorkflowAction"/> to its <see cref="IActionExecutor"/> by
/// <see cref="ActionType"/>. Unknown or unregistered action types are logged and skipped.
/// </summary>
internal sealed class ActionExecutorDispatcher(
    IEnumerable<IActionExecutor> executors,
    ILogger<ActionExecutorDispatcher> logger
) : IActionExecutorDispatcher
{
    private readonly IReadOnlyDictionary<ActionType, IActionExecutor> _map =
        executors.ToDictionary(e => e.ActionType);

    public async Task ExecuteAllAsync(
        IReadOnlyList<WorkflowAction> actions,
        WorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        foreach (var action in actions)
        {
            if (!_map.TryGetValue(action.ActionType, out var executor))
            {
                logger.LogWarning(
                    "No executor registered for ActionType {ActionType}. Action {ActionId} skipped.",
                    action.ActionType, action.Id);
                continue;
            }

            try
            {
                await executor.ExecuteAsync(action, context, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Action executor {ActionType} failed for action {ActionId} on instance {InstanceId}.",
                    action.ActionType, action.Id, context.InstanceId);
                // Individual action failures do not abort the remaining actions.
            }
        }
    }
}
