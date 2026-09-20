namespace Sankore.Modules.Leads.Features.ScoringConfigs.ListScoringConfigs;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListScoringConfigsHandler(LeadsDbContext db)
    : IRequestHandler<ListScoringConfigsQuery, Result<IReadOnlyList<ScoringConfigDto>>>
{
    public async Task<Result<IReadOnlyList<ScoringConfigDto>>> Handle(
        ListScoringConfigsQuery query, CancellationToken ct)
    {
        var q = db.ScoringConfigs.AsQueryable();

        if (query.ActiveOnly == true)
            q = q.Where(c => c.IsActive);

        var configs = await q
            .OrderByDescending(c => c.Version)
            .Select(c => new ScoringConfigDto(
                c.Id,
                c.Version,
                c.Name,
                c.QualificationThreshold,
                c.WeightDemographics,
                c.WeightEngagement,
                c.WeightProduct,
                c.WeightChannel,
                c.WeightRecency,
                c.IsActive,
                c.CreatedAt,
                c.ActivatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ScoringConfigDto>>(configs);
    }
}
