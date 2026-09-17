using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Infrastructure.Actions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure.Consumers;

/// <summary>
/// Listens for <see cref="WorkflowCompletedIntegrationEvent"/> and resumes any parent
/// workflow instance that was suspended (<see cref="WorkflowStatus.WaitingForChild"/>)
/// waiting for the completed child.
///
/// Fires a <see cref="EventCodes.ChildCompleted"/> transition on the parent through the
/// state machine — template designers control what happens next by defining a transition
/// with that event code from the relevant state.
/// </summary>
public sealed class ChildWorkflowCompletedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ChildWorkflowCompletedConsumer> logger
) : IConsumer<WorkflowCompletedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<WorkflowCompletedIntegrationEvent> context)
    {
        var ev = context.Message;
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db               = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var conditionEval    = scope.ServiceProvider.GetRequiredService<IConditionEvaluator>();
        var actionDispatcher = scope.ServiceProvider.GetRequiredService<IActionExecutorDispatcher>();

        // Find the completed child instance and check whether it has a parent.
        var child = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == ev.InstanceId, ct);

        if (child?.ParentInstanceId is null)
            return;

        var parentId = child.ParentInstanceId.Value;

        var parent = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == parentId, ct);

        if (parent is null || parent.Status != WorkflowStatus.WaitingForChild)
            return;

        if (parent.WaitingForChildId != ev.InstanceId)
            return;

        // Resume: set parent back to InProgress so the transition can fire.
        try
        {
            parent.ResumeFromChild(ev.InstanceId);
        }
        catch (DomainException ex)
        {
            logger.LogWarning(
                "ChildWorkflowCompletedConsumer: could not resume parent {ParentId}: {Reason}",
                parentId, ex.Message);
            return;
        }

        // Load CHILD_COMPLETED transitions and their actions.
        var transitions = await db.WorkflowTransitions
            .IgnoreQueryFilters()
            .Where(t => t.TemplateId == parent.TemplateId
                     && t.EventCode  == EventCodes.ChildCompleted)
            .ToListAsync(ct);

        if (transitions.Count == 0)
        {
            // No transition defined — just save the resumed status and leave it to a human.
            db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
                tenantId:      parent.TenantId,
                instanceId:    parent.Id,
                fromStateId:   parent.CurrentStateId,
                toStateId:     parent.CurrentStateId,
                eventCode:     EventCodes.ChildCompleted,
                actedByUserId: Guid.Empty,
                comment:       $"Child instance {ev.InstanceId} completed. No CHILD_COMPLETED transition defined.",
                contextSnapshot: parent.ContextJson));

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "ChildWorkflowCompletedConsumer: parent {ParentId} resumed but has no CHILD_COMPLETED transition.",
                parentId);
            return;
        }

        var transitionIds = transitions.Select(t => t.Id).ToList();
        var actionsByTransitionId = (await db.WorkflowActions
            .IgnoreQueryFilters()
            .Where(a => transitionIds.Contains(a.TransitionId))
            .ToListAsync(ct))
            .GroupBy(a => a.TransitionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<WorkflowAction>)g.ToList());

        try
        {
            parent.AdvanceByEvent(
                EventCodes.ChildCompleted,
                actedByUserId: Guid.Empty,
                comment:       $"Child instance {ev.InstanceId} completed.",
                transitions:   transitions,
                conditionEvaluator:    conditionEval,
                actionsByTransitionId: actionsByTransitionId);
        }
        catch (DomainException ex)
        {
            logger.LogWarning(
                "ChildWorkflowCompletedConsumer: CHILD_COMPLETED transition failed for parent {ParentId}: {Reason}",
                parentId, ex.Message);
            await db.SaveChangesAsync(ct); // still save the resumed status
            return;
        }

        if (parent.PendingAuditEntries.Count > 0)
            db.WorkflowAuditEntries.AddRange(parent.PendingAuditEntries);

        await db.SaveChangesAsync(ct);

        if (parent.PendingActions.Count > 0)
        {
            var workflowContext = new WorkflowContext
            {
                InstanceId    = parent.Id,
                TenantId      = parent.TenantId,
                EntityType    = parent.EntityType,
                EntityId      = parent.EntityId,
                ActedByUserId = Guid.Empty,
                Variables     = new Dictionary<string, object>()
            };
            await actionDispatcher.ExecuteAllAsync(parent.PendingActions, workflowContext, ct);
        }

        logger.LogInformation(
            "ChildWorkflowCompletedConsumer: parent {ParentId} resumed and CHILD_COMPLETED transition fired.",
            parentId);
    }
}
