namespace Sankore.Modules.Leads.Features.TaskTypes.CreateTaskType;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateTaskTypeHandler(LeadsDbContext db)
    : IRequestHandler<CreateTaskTypeCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateTaskTypeCommand cmd, CancellationToken ct)
    {
        var codeExists = await db.TaskTypeConfigs
            .AnyAsync(t => t.TenantId == cmd.TenantId && t.Code == cmd.Code, ct);

        if (codeExists)
            return Result.Fail<Guid>("TASK_TYPE_CODE_ALREADY_EXISTS");

        var taskType = TaskTypeConfig.Create(
            tenantId:     cmd.TenantId,
            code:         cmd.Code,
            label:        cmd.Label,
            description:  cmd.Description,
            displayOrder: cmd.DisplayOrder);

        db.TaskTypeConfigs.Add(taskType);
        await db.SaveChangesAsync(ct);

        return Result.Ok(taskType.Id);
    }
}
