namespace Sankore.Modules.Leads.Features.DispatchingRules.GetDispatchingRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetDispatchingRuleHandler(LeadsDbContext db)
    : IRequestHandler<GetDispatchingRuleQuery, Result<DispatchingRuleDto>>
{
    public async Task<Result<DispatchingRuleDto>> Handle(
        GetDispatchingRuleQuery query, CancellationToken ct)
    {
        var rule = await db.DispatchingRules
            .Where(r => r.Id == query.RuleId)
            .Select(r => new DispatchingRuleDto(
                r.Id,
                r.Name,
                r.Strategy,
                new ScoringWeightsDto(
                    r.Weights.Language,
                    r.Weights.Product,
                    r.Weights.Geography,
                    r.Weights.Workload,
                    r.Weights.Performance,
                    r.Weights.Agency),
                r.MaxLeadsPerAgent,
                r.MaxTasksPerAgent,
                r.AntiMonopolyThreshold,
                r.FirstContactSla,
                r.DeclineExclusionTtl,
                r.IsActive,
                r.Priority,
                r.ExcludedAgentIds))
            .FirstOrDefaultAsync(ct);

        return rule is null
            ? Result.Fail<DispatchingRuleDto>("RULE_NOT_FOUND")
            : Result.Ok(rule);
    }
}
