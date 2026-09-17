using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Starts a subordinate <see cref="WorkflowInstance"/> as a child of the current one.
///
/// Config shape:
/// <code>
/// {
///   "childEntityType": "KycCheck",   // optional — defaults to parent entity type
///   "waitForCompletion": true         // if true, parent is suspended (WaitingForChild)
///                                     // until the child completes
/// }
/// </code>
///
/// When <c>waitForCompletion</c> is true the parent moves to <see cref="WorkflowStatus.WaitingForChild"/>
/// and its SLA clock is paused. The <see cref="ChildWorkflowCompletedConsumer"/> resumes it
/// automatically when the child reaches <see cref="WorkflowStatus.Completed"/>, firing a
/// <see cref="EventCodes.ChildCompleted"/> transition.
/// </summary>
internal sealed class StartChildWorkflowExecutor(
    WorkflowDbContext db,
    IRuleEvaluator ruleEvaluator,
    ILogger<StartChildWorkflowExecutor> logger
) : IActionExecutor
{
    public ActionType ActionType => ActionType.StartChildWorkflow;

    public async Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<ChildWorkflowConfig>(
            action.ConfigJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new ChildWorkflowConfig();

        var childEntityType = string.IsNullOrWhiteSpace(cfg.ChildEntityType)
            ? context.EntityType
            : cfg.ChildEntityType;

        var template = await db.WorkflowTemplates
            .IgnoreQueryFilters()
            .Include(t => t.Steps).ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .FirstOrDefaultAsync(
                t => t.TenantId  == context.TenantId
                  && t.EntityType == childEntityType
                  && t.IsActive,
                ct);

        if (template is null)
        {
            logger.LogWarning(
                "StartChildWorkflow: no active template found for entity type '{ChildEntityType}' " +
                "(parent instance {InstanceId}). Action skipped.",
                childEntityType, context.InstanceId);
            return;
        }

        if (!template.Steps.Any())
        {
            logger.LogWarning(
                "StartChildWorkflow: template {TemplateId} has no steps. Action skipped.",
                template.Id);
            return;
        }

        var contextJson = context.Variables.Count > 0
            ? JsonSerializer.Serialize(context.Variables)
            : "{}";

        var rulesByStepDefId = template.Steps.ToDictionary(
            s => s.Id,
            s => (IReadOnlyCollection<WorkflowRule>)s.Rules);

        var child = WorkflowInstance.Start(
            template, context.EntityId, context.ActedByUserId,
            contextJson, rulesByStepDefId, context.Variables, ruleEvaluator);

        child.SetParent(context.InstanceId);

        db.WorkflowInstances.Add(child);
        db.WorkflowInstanceSteps.AddRange(child.Steps);

        if (cfg.WaitForCompletion)
        {
            var parent = await db.WorkflowInstances
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(i => i.Id == context.InstanceId, ct);

            if (parent is not null)
            {
                try
                {
                    parent.PauseForChild(child.Id);
                }
                catch (DomainException ex)
                {
                    logger.LogWarning(
                        "StartChildWorkflow: could not pause parent {InstanceId}: {Reason}",
                        context.InstanceId, ex.Message);
                }
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "StartChildWorkflow: child instance {ChildId} started under parent {ParentId} " +
            "(waitForCompletion={Wait}).",
            child.Id, context.InstanceId, cfg.WaitForCompletion);
    }

    private sealed record ChildWorkflowConfig(
        string? ChildEntityType = null,
        bool WaitForCompletion  = false);
}
