namespace Sankore.Modules.Leads.Features.ScoringConfigs.GetScoringConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetScoringConfigHandler(LeadsDbContext db)
    : IRequestHandler<GetScoringConfigQuery, Result<ScoringConfigDto>>
{
    public async Task<Result<ScoringConfigDto>> Handle(
        GetScoringConfigQuery query, CancellationToken ct)
    {
        var config = await db.ScoringConfigs
            .Where(c => c.Id == query.ConfigId)
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
            .FirstOrDefaultAsync(ct);

        return config is null
            ? Result.Fail<ScoringConfigDto>("SCORING_CONFIG_NOT_FOUND")
            : Result.Ok(config);
    }
}
