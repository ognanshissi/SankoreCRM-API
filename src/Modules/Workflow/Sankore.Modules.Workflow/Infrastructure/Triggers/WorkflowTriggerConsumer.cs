using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Workflow;

namespace Sankore.Modules.Workflow.Infrastructure.Triggers;

/// <summary>
/// Listens for <see cref="WorkflowTriggerSignal"/> messages on the bus and
/// auto-starts a new <see cref="WorkflowInstance"/> for every active
/// <see cref="WorkflowTrigger"/> whose EntityType + EventName match the signal.
///
/// Runs outside any HTTP/tenant context — uses <see cref="IServiceScopeFactory"/>
/// and <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// with explicit TenantId filtering (same pattern as <see cref="Jobs.SlaCheckerJob"/>).
/// </summary>
public sealed class WorkflowTriggerConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowTriggerConsumer> logger
) : IConsumer<WorkflowTriggerSignal>
{
    public async Task Consume(ConsumeContext<WorkflowTriggerSignal> context)
    {
        var signal = context.Message;
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db               = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var ruleEvaluator    = scope.ServiceProvider.GetRequiredService<IRuleEvaluator>();
        var conditionEvaluator = scope.ServiceProvider.GetRequiredService<IConditionEvaluator>();

        var triggers = await db.WorkflowTriggers
            .IgnoreQueryFilters()
            .Where(t => t.TenantId   == signal.TenantId
                     && t.EventName  == signal.EventName
                     && t.TriggerType == TriggerType.EntityEvent
                     && t.IsActive)
            .ToListAsync(ct);

        if (triggers.Count == 0)
            return;

        var signalContext = signal.Context
            ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var trigger in triggers)
        {
            try
            {
                // Evaluate optional trigger condition before starting an instance.
                if (trigger.ConditionJson is not null &&
                    !conditionEvaluator.Evaluate(trigger.ConditionJson, signalContext))
                {
                    logger.LogDebug(
                        "Trigger {TriggerId} condition not met for signal '{EventName}' on entity {EntityId} — skipped.",
                        trigger.Id, signal.EventName, signal.EntityId);
                    continue;
                }

                await StartInstanceAsync(db, ruleEvaluator, trigger, signal, signalContext, ct);

                logger.LogInformation(
                    "Workflow instance started via trigger {TriggerId} for entity {EntityType}/{EntityId}.",
                    trigger.Id, signal.EntityType, signal.EntityId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to start workflow instance for trigger {TriggerId} (EntityType={EntityType}, EntityId={EntityId}).",
                    trigger.Id, signal.EntityType, signal.EntityId);
            }
        }
    }

    private static async Task StartInstanceAsync(
        WorkflowDbContext db,
        IRuleEvaluator ruleEvaluator,
        WorkflowTrigger trigger,
        WorkflowTriggerSignal signal,
        IReadOnlyDictionary<string, object> context,
        CancellationToken ct)
    {
        var template = await db.WorkflowTemplates
            .IgnoreQueryFilters()
            .Include(t => t.Steps)
                .ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .FirstOrDefaultAsync(
                t => t.Id == trigger.TemplateId
                  && t.TenantId == signal.TenantId
                  && t.IsActive,
                ct);

        if (template is null || !template.Steps.Any())
            return;

        var contextJson = context.Count > 0
            ? JsonSerializer.Serialize(context)
            : "{}";

        var rulesByStepDefId = template.Steps.ToDictionary(
            s => s.Id,
            s => (IReadOnlyCollection<WorkflowRule>)s.Rules);

        // Guid.Empty signals a system-initiated (non-user) start.
        var instance = WorkflowInstance.Start(
            template, signal.EntityId, startedByUserId: Guid.Empty,
            contextJson, rulesByStepDefId, context, ruleEvaluator);

        db.WorkflowInstances.Add(instance);
        db.WorkflowInstanceSteps.AddRange(instance.Steps);
        await db.SaveChangesAsync(ct);
    }
}
