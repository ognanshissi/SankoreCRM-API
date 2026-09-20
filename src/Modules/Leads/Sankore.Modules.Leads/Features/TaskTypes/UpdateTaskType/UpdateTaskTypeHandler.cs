namespace Sankore.Modules.Leads.Features.TaskTypes.UpdateTaskType;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateTaskTypeHandler(LeadsDbContext db)
    : IRequestHandler<UpdateTaskTypeCommand, Result>
{
    public async Task<Result> Handle(
        UpdateTaskTypeCommand cmd, CancellationToken ct)
    {
        var taskType = await db.TaskTypeConfigs
            .AsTracking()
            .FirstOrDefaultAsync(t => t.Id == cmd.TaskTypeId, ct);

        if (taskType is null)
            return Result.Fail("TASK_TYPE_NOT_FOUND");

        taskType.Update(cmd.Label, cmd.Description, cmd.DisplayOrder);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
