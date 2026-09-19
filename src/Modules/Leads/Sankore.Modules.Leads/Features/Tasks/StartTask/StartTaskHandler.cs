namespace Sankore.Modules.Leads.Features.Tasks.StartTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class StartTaskHandler(LeadsDbContext db)
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
