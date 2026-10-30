using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Infrastructure.Actions;

namespace Sankore.Modules.Workflow.Infrastructure.Waits;

/// <summary>
/// Moves an instance from outside a request, as an approval does: loads everything a move needs
/// (transitions, rules, actions, context), applies it, saves, then runs the transition actions and
/// announces a completion. Tenant filters are ignored — every caller is a job or a consumer.
///
/// <para>
/// Two moves: ending an « Attente » step (the SLA job when its delay runs out, the trigger consumer
/// when the awaited event arrives — before, a wait's delay was read as an SLA and ENDED the
/// instance), and closing a « Tâche manuelle » step whose CRM task was completed.
/// </para>
/// </summary>
internal sealed class InstanceRunner(
    WorkflowDbContext db,
    IRuleEvaluator ruleEvaluator,
    IConditionEvaluator conditionEvaluator,
    IActionExecutorDispatcher actionDispatcher,
    IBus bus,
    ILogger<InstanceRunner> logger)
{
    /// <summary>True when the instance was waiting and moved on.</summary>
    public Task<bool> ResumeWaitAsync(Guid instanceId, string reason, CancellationToken ct)
        => MoveAsync(instanceId, (instance, m) => instance.ResumeWait(reason,
            m.Transitions, m.Rules, m.Context, ruleEvaluator, conditionEvaluator, m.Actions), ct);

    /// <summary>True when the integration step was the open one and the circuit moved on.</summary>
    public Task<bool> PassIntegrationAsync(Guid instanceId, Guid instanceStepId, string reason, CancellationToken ct)
        => MoveAsync(instanceId, (instance, m) => instance.PassIntegration(instanceStepId, reason,
            m.Transitions, m.Rules, m.Context, ruleEvaluator, conditionEvaluator, m.Actions), ct);

    /// <summary>True when the KYC step was the open one and the circuit moved on.</summary>
    public Task<bool> PassKycCheckAsync(Guid instanceId, Guid instanceStepId, string reason, CancellationToken ct)
        => MoveAsync(instanceId, (instance, m) => instance.PassKycCheck(instanceStepId, reason,
            m.Transitions, m.Rules, m.Context, ruleEvaluator, conditionEvaluator, m.Actions), ct);

    /// <summary>True when the condition step was the open one and the circuit moved on.</summary>
    public Task<bool> ResolveConditionAsync(Guid instanceId, Guid instanceStepId, bool outcome, string reason, CancellationToken ct)
        => MoveAsync(instanceId, (instance, m) => instance.ResolveCondition(instanceStepId, outcome, reason,
            m.Transitions, m.Rules, m.Context, ruleEvaluator, conditionEvaluator, m.Actions), ct);

    /// <summary>True when the step was the open one and the circuit moved on.</summary>
    public async Task<bool> CompleteTaskStepAsync(Guid tenantId, Guid instanceStepId, Guid actedBy, string comment, CancellationToken ct)
    {
        var instanceId = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Id == instanceStepId)
            .Select(s => (Guid?)s.InstanceId)
            .FirstOrDefaultAsync(ct);
        if (instanceId is null) return false;

        return await MoveAsync(instanceId.Value, (instance, m) => instance.CompleteManualTask(instanceStepId, actedBy, comment,
            m.Transitions, m.Rules, m.Context, ruleEvaluator, conditionEvaluator, m.Actions), ct, actedBy);
    }

    private sealed record Moves(
        IReadOnlyCollection<WorkflowTransition> Transitions,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowRule>> Rules,
        IReadOnlyDictionary<string, object> Context,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<WorkflowAction>> Actions);

    private async Task<bool> MoveAsync(
        Guid instanceId, Func<WorkflowInstance, Moves, bool> move, CancellationToken ct, Guid actedBy = default)
    {
        var instance = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);
        if (instance is null) return false;

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

        if (!move(instance, new Moves(transitions, rulesByStepDefId, context, actionsByTransitionId)))
            return false;

        db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);
        await db.SaveChangesAsync(ct);

        if (instance.PendingActions.Count > 0)
        {
            await actionDispatcher.ExecuteAllAsync(instance.PendingActions, new WorkflowContext
            {
                InstanceId    = instance.Id,
                TenantId      = instance.TenantId,
                EntityType    = instance.EntityType,
                EntityId      = instance.EntityId,
                ActedByUserId = actedBy,
                Variables     = context,
            }, ct);
        }

        if (instance.Status == WorkflowStatus.Completed)
        {
            await bus.Publish(new WorkflowCompletedIntegrationEvent(
                EventId:    Guid.NewGuid(),
                InstanceId: instance.Id,
                TenantId:   instance.TenantId,
                EntityType: instance.EntityType,
                EntityId:   instance.EntityId,
                OccurredAt: DateTimeOffset.UtcNow), ct);
        }

        logger.LogInformation("Instance {InstanceId} moved from outside a request; now {Status}.",
            instance.Id, instance.Status);
        return true;
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
