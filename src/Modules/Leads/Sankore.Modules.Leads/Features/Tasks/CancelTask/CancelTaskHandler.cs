namespace Sankore.Modules.Leads.Features.Tasks.CancelTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CancelTaskHandler(LeadsDbContext db, AgentCapacityService capacityService)
    : IRequestHandler<CancelTaskCommand, Result>
{
    public async Task<Result> Handle(CancelTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        var agentId  = task.AssignedAgentId;
        var tenantId = task.TenantId;

        var result = task.Cancel();
        if (result.IsFailure) return result;

        await db.SaveChangesAsync(ct);

        if (agentId.HasValue)
            await capacityService.InvalidateAsync(tenantId, agentId.Value, ct);

        return Result.Ok();
    }
}
