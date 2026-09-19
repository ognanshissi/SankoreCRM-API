namespace Sankore.Modules.Leads.Features.Tasks.CompleteTask;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CompleteTaskHandler(LeadsDbContext db)
    : IRequestHandler<CompleteTaskCommand, Result>
{
    public async Task<Result> Handle(CompleteTaskCommand cmd, CancellationToken ct)
    {
        var task = await db.CrmTasks.AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskId, ct);

        if (task is null)
            return Result.Fail("TASK_NOT_FOUND");

        var result = task.Complete();
        if (result.IsFailure) return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
