namespace Sankore.Modules.Leads.Features.PipelineStages.ListPipelineStages;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListPipelineStagesHandler(LeadsDbContext db)
    : IRequestHandler<ListPipelineStagesQuery, Result<IReadOnlyList<PipelineStageConfigDto>>>
{
    public async Task<Result<IReadOnlyList<PipelineStageConfigDto>>> Handle(
        ListPipelineStagesQuery query, CancellationToken ct)
    {
        var q = db.PipelineStageConfigs.AsQueryable();

        if (query.ActiveOnly == true)
            q = q.Where(s => s.IsActive);

        var stages = await q
            .OrderBy(s => s.DisplayOrder)
            .Select(s => new PipelineStageConfigDto(
                s.Id,
                s.Code,
                s.Label,
                s.Description,
                s.DisplayOrder,
                s.Color,
                s.IsActive,
                s.IsSystem,
                s.IsFinal,
                s.CreatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<PipelineStageConfigDto>>(stages);
    }
}
