namespace Sankore.Modules.Leads.Features.DispatchingRules.CreateDispatchingRule;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateDispatchingRuleHandler(LeadsDbContext db)
    : IRequestHandler<CreateDispatchingRuleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateDispatchingRuleCommand cmd, CancellationToken ct)
    {
        var rule = DispatchingRule.Create(
            tenantId:             cmd.TenantId,
            name:                 cmd.Name,
            strategy:             cmd.Strategy,
            weights:              new ScoringWeights(
                                      cmd.Weights.Language,
                                      cmd.Weights.Product,
                                      cmd.Weights.Geography,
                                      cmd.Weights.Workload,
                                      cmd.Weights.Performance),
            maxLeadsPerAgent:      cmd.MaxLeadsPerAgent,
            antiMonopolyThreshold: cmd.AntiMonopolyThreshold,
            firstContactSla:       cmd.FirstContactSla,
            priority:              cmd.Priority,
            excludedAgentIds:      cmd.ExcludedAgentIds);

        db.DispatchingRules.Add(rule);
        await db.SaveChangesAsync(ct);

        return Result.Ok(rule.Id);
    }
}
