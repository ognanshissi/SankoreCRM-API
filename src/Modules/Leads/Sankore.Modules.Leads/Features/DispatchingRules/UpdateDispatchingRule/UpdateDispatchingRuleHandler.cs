namespace Sankore.Modules.Leads.Features.DispatchingRules.UpdateDispatchingRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateDispatchingRuleHandler(LeadsDbContext db)
    : IRequestHandler<UpdateDispatchingRuleCommand, Result>
{
    public async Task<Result> Handle(
        UpdateDispatchingRuleCommand cmd, CancellationToken ct)
    {
        var rule = await db.DispatchingRules
            .AsTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null)
            return Result.Fail("RULE_NOT_FOUND");

        rule.Update(
            name:                  cmd.Name,
            weights:               new ScoringWeights(
                                       cmd.Weights.Language,
                                       cmd.Weights.Product,
                                       cmd.Weights.Geography,
                                       cmd.Weights.Workload,
                                       cmd.Weights.Performance,
                                       cmd.Weights.Agency),
            maxLeadsPerAgent:      cmd.MaxLeadsPerAgent,
            maxTasksPerAgent:      cmd.MaxTasksPerAgent,
            antiMonopolyThreshold: cmd.AntiMonopolyThreshold,
            firstContactSla:       cmd.FirstContactSla,
            priority:              cmd.Priority,
            excludedAgentIds:      cmd.ExcludedAgentIds ?? []);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
