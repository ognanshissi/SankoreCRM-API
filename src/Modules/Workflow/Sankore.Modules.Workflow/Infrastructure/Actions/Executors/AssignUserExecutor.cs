using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Stub executor for assigning a user to the workflow entity.
/// Full implementation requires a Phase-7 IContextProvider per entity type.
/// Config shape: <c>{ "userId": "..." }</c> or <c>{ "roleCode": "..." }</c> for round-robin.
/// </summary>
internal sealed class AssignUserExecutor(ILogger<AssignUserExecutor> logger) : IActionExecutor
{
    public ActionType ActionType => ActionType.AssignUser;

    public Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        logger.LogInformation(
            "AssignUser action {ActionId} queued for instance {InstanceId} (entity {EntityType}/{EntityId}). " +
            "Full assignment requires a context provider — implement in Phase 7.",
            action.Id, context.InstanceId, context.EntityType, context.EntityId);
        return Task.CompletedTask;
    }
}
