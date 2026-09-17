using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Stub executor for starting a child workflow instance as a sub-process.
/// Full implementation requires resolving the target template by entity type.
/// Config shape: <c>{ "templateEntityType": "KycCheck" }</c>
/// </summary>
internal sealed class StartChildWorkflowExecutor(ILogger<StartChildWorkflowExecutor> logger) : IActionExecutor
{
    public ActionType ActionType => ActionType.StartChildWorkflow;

    public Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        logger.LogInformation(
            "StartChildWorkflow action {ActionId} queued for instance {InstanceId}. " +
            "Full child workflow start will be implemented in a future phase.",
            action.Id, context.InstanceId);
        return Task.CompletedTask;
    }
}
