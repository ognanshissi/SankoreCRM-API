namespace Sankore.Modules.Leads.Features.Tasks.StartTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

// StartTask moves a task from Pending → InProgress; the open task count
// does not change (both statuses count as open) so no cache invalidation is
// needed. The handler is still injected with AgentCapacityService to maintain
// consistent constructor signatures across all task handlers.
internal sealed class StartTaskHandler(LeadsDbContext db, AgentCapacityService capacityService)
    : IRequestHandler<StartTaskCommand, Result>
{
    public async Task<Result> Handle(StartTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        var result = task.StartProgress();
        if (result.IsFailure) return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
