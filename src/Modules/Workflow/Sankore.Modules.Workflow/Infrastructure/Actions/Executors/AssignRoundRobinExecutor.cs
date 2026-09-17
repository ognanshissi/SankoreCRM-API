using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Stub executor for round-robin assignment within a role.
/// Full implementation requires a Phase-7 IContextProvider + user roster query.
/// Config shape: <c>{ "roleCode": "Agent" }</c>
/// </summary>
internal sealed class AssignRoundRobinExecutor(ILogger<AssignRoundRobinExecutor> logger) : IActionExecutor
{
    public ActionType ActionType => ActionType.AssignRoundRobin;

    public Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        logger.LogInformation(
            "AssignRoundRobin action {ActionId} queued for instance {InstanceId}. " +
            "Full round-robin logic requires a context provider — implement in Phase 7.",
            action.Id, context.InstanceId);
        return Task.CompletedTask;
    }
}
