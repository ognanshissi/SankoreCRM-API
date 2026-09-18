namespace Sankore.Modules.Leads.Features.DispatchingRules.ListDispatchingRules;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListDispatchingRulesHandler(LeadsDbContext db)
    : IRequestHandler<ListDispatchingRulesQuery, Result<IReadOnlyList<DispatchingRuleDto>>>
{
    public async Task<Result<IReadOnlyList<DispatchingRuleDto>>> Handle(
        ListDispatchingRulesQuery query, CancellationToken ct)
    {
        var q = db.DispatchingRules.AsQueryable();

        if (query.ActiveOnly == true)
            q = q.Where(r => r.IsActive);

        var rules = await q
            .OrderBy(r => r.Name)
            .Select(r => new DispatchingRuleDto(
                r.Id,
                r.Name,
                r.Strategy,
                new ScoringWeightsDto(
                    r.Weights.Language,
                    r.Weights.Product,
                    r.Weights.Geography,
                    r.Weights.Workload,
                    r.Weights.Performance),
                r.MaxLeadsPerAgent,
                r.AntiMonopolyThreshold,
                r.FirstContactSla,
                r.IsActive))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<DispatchingRuleDto>>(rules);
    }
}
