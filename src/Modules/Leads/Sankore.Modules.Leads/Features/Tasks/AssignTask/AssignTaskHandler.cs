namespace Sankore.Modules.Leads.Features.Tasks.AssignTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class AssignTaskHandler(LeadsDbContext db)
    : IRequestHandler<AssignTaskCommand, Result>
{
    public async Task<Result> Handle(AssignTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        task.AssignTo(cmd.AgentId);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
