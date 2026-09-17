using System.Text.Json;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ApproveStep;

internal sealed class ApproveStepHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser,
    IBus bus,
    IRuleEvaluator evaluator,
    IConditionEvaluator conditionEvaluator,
    IActionExecutorDispatcher actionDispatcher
) : IRequestHandler<ApproveStepCommand, Result>
{
    public async Task<Result> Handle(ApproveStepCommand request, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);

        if (instance is null)
            return Result.Fail($"Instance {request.InstanceId} not found.");

        if (instance.Status is not WorkflowStatus.InProgress)
            return Result.Fail($"Instance is not in progress (current status: {instance.Status}).");

        var currentStep = instance.Steps
            .FirstOrDefault(s => s.Order == instance.CurrentStepOrder
                              && s.Status == StepStatus.AwaitingApproval);

        if (currentStep?.ApproverRoleCode is { } requiredRole &&
            !currentUser.Roles.Contains(requiredRole, StringComparer.OrdinalIgnoreCase))
        {
            return Result.Fail(
                $"Step {instance.CurrentStepOrder} requires role '{requiredRole}'. " +
                "You do not have the required role.");
        }

        // Load transitions (state-machine path) and step definitions with rules (legacy path).
        var transitions = await db.WorkflowTransitions
            .Where(t => t.TemplateId == instance.TemplateId)
            .AsNoTracking()
            .ToListAsync(ct);

        var stepDefs = await db.WorkflowStepDefinitions
            .Include(s => s.Rules)
            .Where(s => s.TemplateId == instance.TemplateId)
            .AsNoTracking()
            .ToListAsync(ct);

        var rulesByStepDefId = stepDefs.ToDictionary(
            s => s.Id,
            s => (IReadOnlyCollection<WorkflowRule>)s.Rules);

        // Load actions keyed by transition id.
        var transitionIds = transitions.Select(t => t.Id).ToList();
        var allActions = await db.WorkflowActions
            .Where(a => transitionIds.Contains(a.TransitionId))
            .AsNoTracking()
            .ToListAsync(ct);

        var actionsByTransitionId = allActions
            .GroupBy(a => a.TransitionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<WorkflowAction>)g.OrderBy(a => a.ExecutionOrder).ToList());

        var context = DeserializeContext(instance.ContextJson);

        try
        {
            instance.Approve(currentUser.Id, request.Comment, transitions, rulesByStepDefId,
                context, evaluator, conditionEvaluator, actionsByTransitionId);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        if (instance.PendingAuditEntries.Count > 0)
            db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);

        await db.SaveChangesAsync(ct);

        // Dispatch actions collected by Fire() after persisting the state change.
        if (instance.PendingActions.Count > 0)
        {
            var wfContext = new WorkflowContext
            {
                InstanceId    = instance.Id,
                TenantId      = instance.TenantId,
                EntityType    = instance.EntityType,
                EntityId      = instance.EntityId,
                ActedByUserId = currentUser.Id,
                Variables     = context
            };
            await actionDispatcher.ExecuteAllAsync(instance.PendingActions, wfContext, ct);
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

        return Result.Ok();
    }

    private static IReadOnlyDictionary<string, object> DeserializeContext(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new Dictionary<string, object>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d,
                    JsonValueKind.True  => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Array => prop.Value.EnumerateArray()
                                              .Select(e => e.ToString()).ToArray(),
                    _                   => prop.Value.GetString() ?? string.Empty
                };
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }
}
