namespace Sankore.Modules.Leads.Features.Tasks.AssignTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class AssignTaskHandler(LeadsDbContext db, AgentCapacityService capacityService)
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

        // Invalidate capacity for both the previous assignee (loses the task)
        // and the new one (gains it).
        if (previousAgentId.HasValue)
            await capacityService.InvalidateAsync(tenantId, previousAgentId.Value, ct);
        await capacityService.InvalidateAsync(tenantId, cmd.AgentId, ct);

        return Result.Ok();
    }
}
