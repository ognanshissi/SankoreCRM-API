using Sankore.Shared.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Workflow.PublicApi;
namespace Sankore.Modules.Leads.Features.Tasks.CompleteTask;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class CompleteTaskHandler(
    LeadsDbContext db,
    AgentCapacityService capacityService,
    IBus bus,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<CompleteTaskCommand, Result>
{
    public async Task<Result> Handle(CompleteTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        // Ownership guard: only the assigned agent or a supervisor can complete
        if (task.AssignedAgentId.HasValue
            && task.AssignedAgentId.Value != currentUser.Id
            && !IsSupervisor())
        {
            return Result.Fail("NOT_TASK_OWNER");
        }

        var agentId  = task.AssignedAgentId;
        var tenantId = task.TenantId;

        var result = task.Complete();
        if (result.IsFailure) return result;

        await RecordFirstContactAsync(task, ct);

        // The task of a workflow « Tâche manuelle » step: done means the step is done.
        if (task.TriggerEventType == WorkflowTaskRequested.TriggerType && task.TriggerEventId is { } stepId)
            await publisher.PublishAsync(new WorkflowStepTaskClosed(task.TenantId, stepId, true, currentUser.Id, task.Title), ct);

        await db.SaveChangesAsync(ct);

        if (agentId.HasValue)
            await capacityService.InvalidateAsync(tenantId, agentId.Value, ct);

        // Publish event for downstream rule evaluation (US-M13-093)
        await bus.Publish(new TaskCompletedEvent(
            TaskId:          task.Id,
            TenantId:        tenantId,
            LeadId:          task.LeadId,
            AssignedAgentId: agentId,
            TaskType:        task.Type.ToString()), ct);

        return Result.Ok();
    }

    /// <summary>
    /// The dispatch's « Premier contact » task marked done IS the agent declaring the call, as the
    /// « 1er contact eu » button does. Before, only the reverse was wired (recording the contact
    /// closed the task): completing the task left the assignment owing its first contact, so the
    /// lead's next step came back as « Premier contact » right after the agent ticked it off, the
    /// SLA kept running and the lead stayed « Nouveau ».
    /// </summary>
    private async Task RecordFirstContactAsync(CrmTask task, CancellationToken ct)
    {
        if (task.Type != CrmTaskType.FirstContact
            || task.TriggerEventType != TaskTriggerEvents.LeadDispatched
            || task.LeadId is not { } leadId)
            return;

        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead?.CurrentAssignmentId is not { } assignmentId) return;

        var assignment = await db.LeadAssignments.AsTracking()
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        // Only the CURRENT assignment's task: one left over from a reassignment says nothing about
        // whether the agent who has the lead now has called.
        if (assignment is null || assignment.AgentId != task.AssignedAgentId) return;

        if (assignment.RecordFirstContact(DateTimeOffset.UtcNow, FirstContactSource.AgentDeclared))
            lead.RecordContactEstablished();
    }

    private bool IsSupervisor() =>
        currentUser.Roles.Any(r =>
            r is "System" or "Administrator" or "SalesManager" or "BranchManager");
}
