namespace Sankore.Modules.Leads.Features.TaskTypes.ActivateTaskType;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateTaskTypeHandler(LeadsDbContext db)
    : IRequestHandler<ActivateTaskTypeCommand, Result>
{
    public async Task<Result> Handle(
        ActivateTaskTypeCommand cmd, CancellationToken ct)
    {
        var taskType = await db.TaskTypeConfigs
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskTypeId, ct);

        if (taskType is null)
            return Result.Fail("TASK_TYPE_NOT_FOUND");

        taskType.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
