namespace Sankore.Modules.Leads.Features.Tasks.CompleteTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CompleteTaskHandler(LeadsDbContext db, AgentCapacityService capacityService)
    : IRequestHandler<CompleteTaskCommand, Result>
{
    public async Task<Result> Handle(CompleteTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        var agentId = task.AssignedAgentId;
        var tenantId = task.TenantId;

        var result = task.Complete();
        if (result.IsFailure) return result;

        await db.SaveChangesAsync(ct);

        if (agentId.HasValue)
            await capacityService.InvalidateAsync(tenantId, agentId.Value, ct);

        return Result.Ok();
    }
}
