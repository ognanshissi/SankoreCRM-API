using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Infrastructure.Actions;

namespace Sankore.Modules.Workflow.Infrastructure.Notifications;

/// <summary>
/// Once per start, moves on the instances sitting on an automatic step that is still open.
///
/// <para>
/// Before the engine ran notification steps, an instance reaching one waited for an approval: the
/// step has no role, so it was in nobody's « mes étapes » and the dossier stayed there. Those
/// instances are released here — the notification is requested like any other — rather than left
/// for someone to find. Afterwards the query finds nothing: an open automatic step only exists when
/// no transition leads out of it, and that one is left to a human on purpose.
/// </para>
///
/// <para>
/// After <c>ApplicationStarted</c> and not in <c>InitializeAsync</c>: moving an instance may run
/// transition actions and announce a completion on the bus, which is not up during migrations.
/// </para>
/// </summary>
internal sealed class AutomaticStepsCatchUp(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<AutomaticStepsCatchUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WhenStartedAsync(stoppingToken);
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AutomaticStepsCatchUp failed.");
        }
    }

    private Task WhenStartedAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource();
        lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        ct.Register(() => started.TrySetCanceled(ct));
        return started.Task;
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        List<Guid> instanceIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            // IgnoreQueryFilters: every tenant, outside any request.
            var openSteps = await db.WorkflowInstanceSteps
                .IgnoreQueryFilters()
                .Where(s => s.Status == StepStatus.AwaitingApproval)
                .Select(s => new { s.InstanceId, s.Kind })
                .ToListAsync(ct);
            instanceIds = openSteps
                .Where(s => StepKinds.RunsAutomatically(s.Kind))
                .Select(s => s.InstanceId)
                .Distinct()
                .ToList();
        }

        if (instanceIds.Count == 0) return;

        logger.LogInformation("AutomaticStepsCatchUp: {Count} instance(s) waiting on an automatic step.", instanceIds.Count);

        foreach (var instanceId in instanceIds)
        {
            try
            {
                await ResumeAsync(instanceId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AutomaticStepsCatchUp: could not move instance {InstanceId}.", instanceId);
            }
        }
    }

    private async Task ResumeAsync(Guid instanceId, CancellationToken ct)
    {
        // One scope per instance: a failed save must not leave its changes tracked for the next one.
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<WorkflowDbContext>();

        var instance = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);
        if (instance is null) return;

        var transitions = await db.WorkflowTransitions
            .IgnoreQueryFilters()
            .Where(t => t.TemplateId == instance.TemplateId)
            .ToListAsync(ct);

        var rulesByStepDefId = (await db.WorkflowStepDefinitions
                .IgnoreQueryFilters()
                .Include(s => s.Rules)
                .Where(s => s.TemplateId == instance.TemplateId)
                .ToListAsync(ct))
            .ToDictionary(s => s.Id, s => (IReadOnlyCollection<WorkflowRule>)s.Rules);

        var transitionIds = transitions.Select(t => t.Id).ToList();
        var actionsByTransitionId = (await db.WorkflowActions
                .IgnoreQueryFilters()
                .Where(a => transitionIds.Contains(a.TransitionId))
                .ToListAsync(ct))
            .GroupBy(a => a.TransitionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<WorkflowAction>)g.OrderBy(a => a.ExecutionOrder).ToList());

        var context = ReadContext(instance.ContextJson);

        var moved = instance.ResumeAutomaticSteps(
            transitions, rulesByStepDefId, context,
            sp.GetRequiredService<IRuleEvaluator>(),
            sp.GetRequiredService<IConditionEvaluator>(),
            actionsByTransitionId);
        if (!moved) return;

        if (instance.PendingAuditEntries.Count > 0)
            db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);

        await db.SaveChangesAsync(ct);

        if (instance.PendingActions.Count > 0)
        {
            await sp.GetRequiredService<IActionExecutorDispatcher>().ExecuteAllAsync(instance.PendingActions, new WorkflowContext
            {
                InstanceId    = instance.Id,
                TenantId      = instance.TenantId,
                EntityType    = instance.EntityType,
                EntityId      = instance.EntityId,
                ActedByUserId = Guid.Empty,
                Variables     = context,
            }, ct);
        }

        if (instance.Status == WorkflowStatus.Completed)
        {
            await sp.GetRequiredService<IBus>().Publish(new WorkflowCompletedIntegrationEvent(
                EventId:    Guid.NewGuid(),
                InstanceId: instance.Id,
                TenantId:   instance.TenantId,
                EntityType: instance.EntityType,
                EntityId:   instance.EntityId,
                OccurredAt: DateTimeOffset.UtcNow), ct);
        }

        logger.LogInformation(
            "AutomaticStepsCatchUp: instance {InstanceId} moved past its automatic step (now {Status}).",
            instance.Id, instance.Status);
    }

    private static IReadOnlyDictionary<string, object> ReadContext(string json)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d,
                    JsonValueKind.True  => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Array => prop.Value.EnumerateArray().Select(e => e.ToString()).ToArray(),
                    _                   => prop.Value.GetString() ?? string.Empty,
                };
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
