namespace Sankore.Modules.Leads.Features.Tasks.AssignTask;

using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class AssignTaskHandler(
    LeadsDbContext db,
    AgentCapacityService capacityService,
    IBus bus)
    : IRequestHandler<AssignTaskCommand, Result>
{
    public async Task<Result> Handle(AssignTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        var previousAgentId = task.AssignedAgentId;
        var tenantId        = task.TenantId;

        task.AssignTo(cmd.AgentId);
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
