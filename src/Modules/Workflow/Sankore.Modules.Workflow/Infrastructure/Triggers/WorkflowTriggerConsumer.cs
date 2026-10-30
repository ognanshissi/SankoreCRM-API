using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Waits;
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

        // A partner's event name and one of the CRM's own never match each other (webhooks lot 6):
        // a partner naming its event LEAD_CAPTURED must not start the capture journeys.
        var triggerType = signal.IsExternal ? TriggerType.ExternalEvent : TriggerType.EntityEvent;

        // The same signal ends the « Attente » steps waiting for it on this very record.
        if (!signal.IsExternal)
            await ResumeWaitsAsync(scope.ServiceProvider, db, signal, ct);

        // A client's KYC decided: the « Vérification KYC » steps depending on that client re-check.
        if (!signal.IsExternal && signal.EventName is "client.kyc_validated" or "client.kyc_rejected")
            await scope.ServiceProvider.GetRequiredService<Gates.KycStepRunner>()
                .ReevaluateForClientAsync(signal.TenantId, signal.EntityId, ct);

        var triggers = await db.WorkflowTriggers
            .IgnoreQueryFilters()
            .Where(t => t.TenantId   == signal.TenantId
                     && t.EventName  == signal.EventName
                     && t.TriggerType == triggerType
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

    private async Task ResumeWaitsAsync(
        IServiceProvider services, WorkflowDbContext db, WorkflowTriggerSignal signal, CancellationToken ct)
    {
        var waiting = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == signal.TenantId
                     && s.Kind == StepKind.Wait
                     && s.Status == StepStatus.AwaitingApproval)
            .Join(db.WorkflowInstances.IgnoreQueryFilters()
                    .Where(i => i.TenantId == signal.TenantId
                             && i.EntityId == signal.EntityId
                             && i.EntityType == signal.EntityType
                             && i.Status == WorkflowStatus.InProgress),
                s => s.InstanceId, i => i.Id, (s, i) => new { s.InstanceId, s.ConfigJson })
            .ToListAsync(ct);

        var matching = waiting.Where(w => WaitStepConfig.EventCode(w.ConfigJson) == signal.EventName).ToList();
        if (matching.Count == 0) return;

        var runner = services.GetRequiredService<InstanceRunner>();
        foreach (var wait in matching)
        {
            try
            {
                await runner.ResumeWaitAsync(wait.InstanceId, WaitStepConfig.ReceivedReason(signal.EventName), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resume the wait of instance {InstanceId} on '{EventName}'.",
                    wait.InstanceId, signal.EventName);
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

        // A signal names an entity; a trigger set on a circuit of another entity must not start
        // that circuit on this id — client.created on a lead circuit would open a lead workflow on
        // a client's id. Every internal event now comes with its entity (PlatformEvents), so the
        // check holds for all of them, not only partners' events.
        if (!string.Equals(template.EntityType, signal.EntityType, StringComparison.OrdinalIgnoreCase))
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
