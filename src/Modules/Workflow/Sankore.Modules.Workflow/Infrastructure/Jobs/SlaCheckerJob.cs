using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Actions;

namespace Sankore.Modules.Workflow.Infrastructure.Jobs;

/// <summary>
/// Background service that runs every minute and times out workflow steps
/// whose SLA deadline (DueAt) has passed without an approver decision.
///
/// Escalation routing: when the template defines a TIMEOUT transition from the
/// current state, the job fires the transition through the state machine (allowing
/// designers to route to a supervisor step, send notifications, etc.). If no TIMEOUT
/// transition exists the job falls back to hard-termination via
/// <see cref="WorkflowInstance.TimeoutCurrentStep"/>.
///
/// Uses IgnoreQueryFilters() because it runs outside any HTTP/tenant context.
/// </summary>
internal sealed class SlaCheckerJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SlaCheckerJob> logger
) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SlaCheckerJob started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await CheckSlaDeadlinesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SlaCheckerJob encountered an error during SLA check.");
            }
        }
    }

    private async Task CheckSlaDeadlinesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var now = DateTimeOffset.UtcNow;

        // Find all instance IDs where the active step has exceeded its DueAt.
        // IgnoreQueryFilters: no HTTP context in a background job — tenant filter skipped intentionally.
        var timedOutInstanceIds = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.Status == StepStatus.AwaitingApproval
                     && s.DueAt.HasValue
                     && s.DueAt.Value <= now)
            .Select(s => s.InstanceId)
            .Distinct()
            .ToListAsync(ct);

        if (timedOutInstanceIds.Count == 0)
            return;

        logger.LogInformation(
            "SlaCheckerJob: {Count} workflow instance(s) have exceeded their SLA deadline.",
            timedOutInstanceIds.Count);

        var conditionEvaluator = scope.ServiceProvider.GetRequiredService<IConditionEvaluator>();
        var actionDispatcher   = scope.ServiceProvider.GetRequiredService<IActionExecutorDispatcher>();

        foreach (var instanceId in timedOutInstanceIds)
        {
            try
            {
                await TimeoutInstanceAsync(db, conditionEvaluator, actionDispatcher, instanceId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SlaCheckerJob: failed to time out workflow instance {InstanceId}.", instanceId);
            }
        }
    }

    private async Task TimeoutInstanceAsync(
        WorkflowDbContext db,
        IConditionEvaluator conditionEvaluator,
        IActionExecutorDispatcher actionDispatcher,
        Guid instanceId,
        CancellationToken ct)
    {
        // Load the instance with its steps using tracking so EF picks up domain mutations.
        var instance = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);

        if (instance is null
            || instance.Status is WorkflowStatus.Completed
                or WorkflowStatus.Rejected
                or WorkflowStatus.Cancelled
                or WorkflowStatus.TimedOut
                or WorkflowStatus.WaitingForChild)
            return;

        // Check whether the template defines a TIMEOUT escalation transition.
        var timeoutTransitions = await db.WorkflowTransitions
            .IgnoreQueryFilters()
            .Where(t => t.TemplateId == instance.TemplateId
                     && t.EventCode  == EventCodes.Timeout)
            .ToListAsync(ct);

        if (timeoutTransitions.Count > 0)
        {
            // Load actions for transitions so they can be dispatched after save.
            var transitionIds = timeoutTransitions.Select(t => t.Id).ToList();
            var actionsByTransitionId = (await db.WorkflowActions
                .IgnoreQueryFilters()
                .Where(a => transitionIds.Contains(a.TransitionId))
                .ToListAsync(ct))
                .GroupBy(a => a.TransitionId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyCollection<WorkflowAction>)g.ToList());

            // Route through state machine — template author controls the next step.
            instance.AdvanceByEvent(
                EventCodes.Timeout,
                actedByUserId: Guid.Empty,
                transitions: timeoutTransitions,
                conditionEvaluator: conditionEvaluator,
                actionsByTransitionId: actionsByTransitionId);

            if (instance.PendingAuditEntries.Count > 0)
                db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);

            await db.SaveChangesAsync(ct);

            // Dispatch actions outside the transaction.
            if (instance.PendingActions.Count > 0)
            {
                var context = new WorkflowContext
                {
                    InstanceId    = instance.Id,
                    TenantId      = instance.TenantId,
                    EntityType    = instance.EntityType,
                    EntityId      = instance.EntityId,
                    ActedByUserId = Guid.Empty,
                    Variables     = new Dictionary<string, object>()
                };

                await actionDispatcher.ExecuteAllAsync(instance.PendingActions, context, ct);
            }

            logger.LogInformation(
                "SlaCheckerJob: instance {InstanceId} routed via TIMEOUT transition (escalation).",
                instanceId);
        }
        else
        {
            // No escalation path configured — hard-terminate the workflow.
            instance.TimeoutCurrentStep();

            if (instance.PendingAuditEntries.Count > 0)
                db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "SlaCheckerJob: instance {InstanceId} hard-terminated (no TIMEOUT transition defined).",
                instanceId);
        }
    }
}
