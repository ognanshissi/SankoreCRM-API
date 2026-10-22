namespace Sankore.Modules.Leads.Features.Tasks.AssignTask;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks.Events;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class AssignTaskHandler(
    LeadsDbContext db,
    AgentCapacityService capacityService,
    IBus bus,
    TaskAccess access,
    ICurrentUser me)
    : IRequestHandler<AssignTaskCommand, Result>
{
    public async Task<Result> Handle(AssignTaskCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: true, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);
        var task = loaded.Value!;

        var previousAgentId = task.AssignedAgentId;
        var tenantId        = task.TenantId;

        task.AssignTo(cmd.AgentId);

        // Every change of holder is in the history, this one included — it used to be the only
        // assignment the history did not show.
        if (previousAgentId != cmd.AgentId)
            db.TaskReassignments.Add(TaskReassignment.Create(
                tenantId, task.Id, previousAgentId, cmd.AgentId, "Assignation directe", me.Id,
                previousSlaDeadline: task.SlaDeadline));

        await db.SaveChangesAsync(ct);

        if (previousAgentId.HasValue)
            await capacityService.InvalidateAsync(tenantId, previousAgentId.Value, ct);
        await capacityService.InvalidateAsync(tenantId, cmd.AgentId, ct);

        await bus.Publish(new TaskAssignedIntegrationEvent(
            TaskId:  task.Id,
            TenantId: tenantId,
            AgentId: cmd.AgentId,
            Title:   task.Title,
            DueAt:   task.DueAt,
            LeadId:  task.LeadId), ct);

        return Result.Ok();
    }
}
