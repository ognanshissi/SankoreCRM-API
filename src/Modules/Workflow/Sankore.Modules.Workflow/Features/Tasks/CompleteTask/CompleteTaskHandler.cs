using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Tasks.CompleteTask;

internal sealed class CompleteTaskHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser,
    IConditionEvaluator conditionEvaluator,
    IActionExecutorDispatcher actionDispatcher
) : IRequestHandler<CompleteTaskCommand, Result>
{
    public async Task<Result> Handle(CompleteTaskCommand request, CancellationToken ct)
    {
        var task = await db.WorkflowTasks
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId && t.InstanceId == request.InstanceId, ct);

        if (task is null)
            return Result.Fail($"Task {request.TaskId} not found.");

        try
        {
            task.Complete(currentUser.Id, request.Comment);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);

        // Fire TASK_COMPLETED event on the parent instance to advance the state machine.
        var instance = await db.WorkflowInstances
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == request.InstanceId, ct);

        if (instance is null || instance.Status is not WorkflowStatus.InProgress)
            return Result.Ok(); // Instance already finished — task completion is still persisted.

        var transitions = await db.WorkflowTransitions
            .Where(t => t.TemplateId == instance.TemplateId)
            .AsNoTracking()
            .ToListAsync(ct);

        if (transitions.Count == 0)
            return Result.Ok();

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
            instance.AdvanceByEvent(
                EventCodes.TaskCompleted,
                currentUser.Id,
                request.Comment,
                transitions,
                context,
                conditionEvaluator,
                actionsByTransitionId);
        }
        catch (DomainException)
        {
            // No matching TASK_COMPLETED transition configured — that's acceptable.
            return Result.Ok();
        }

        if (instance.PendingAuditEntries.Count > 0)
            db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);

        await db.SaveChangesAsync(ct);

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
                    JsonValueKind.Array => prop.Value.EnumerateArray().Select(e => e.ToString()).ToArray(),
                    _                   => prop.Value.GetString() ?? string.Empty
                };
            }
            return result;
        }
        catch { return new Dictionary<string, object>(); }
    }
}
