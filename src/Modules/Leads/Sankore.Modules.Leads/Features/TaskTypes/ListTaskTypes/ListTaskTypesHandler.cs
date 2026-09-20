namespace Sankore.Modules.Leads.Features.TaskTypes.ListTaskTypes;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListTaskTypesHandler(LeadsDbContext db)
    : IRequestHandler<ListTaskTypesQuery, Result<IReadOnlyList<TaskTypeDto>>>
{
    public async Task<Result<IReadOnlyList<TaskTypeDto>>> Handle(
        ListTaskTypesQuery query, CancellationToken ct)
    {
        var q = db.TaskTypeConfigs.AsQueryable();

        if (query.ActiveOnly == true)
            q = q.Where(t => t.IsActive);

        var types = await q
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Label)
            .Select(t => new TaskTypeDto(
                t.Id,
                t.Code,
                t.Label,
                t.Description,
                t.IsActive,
                t.IsSystem,
                t.DisplayOrder,
                t.CreatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<TaskTypeDto>>(types);
    }
}
