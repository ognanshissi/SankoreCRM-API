namespace Sankore.Modules.Leads.Features.TaskTypes.DeactivateTaskType;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateTaskTypeHandler(LeadsDbContext db)
    : IRequestHandler<DeactivateTaskTypeCommand, Result>
{
    public async Task<Result> Handle(
        DeactivateTaskTypeCommand cmd, CancellationToken ct)
    {
        var taskType = await db.TaskTypeConfigs
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskTypeId, ct);

        if (taskType is null)
            return Result.Fail("TASK_TYPE_NOT_FOUND");

        var result = taskType.Deactivate();
        if (!result.IsSuccess)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
