namespace Sankore.Modules.Leads.Features.DispatchingRules.SimulateDispatch;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SimulateDispatchHandler(
    LeadsDbContext db,
    IAdministrationModule usersModule,
    CompatibilityScorer scorer,
    DispatchingStrategyFactory strategyFactory)
    : IRequestHandler<SimulateDispatchQuery, Result<SimulateDispatchResult>>
{
    public async Task<Result<SimulateDispatchResult>> Handle(
        SimulateDispatchQuery query, CancellationToken ct)
    {
        var rule = await db.DispatchingRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == query.RuleId, ct);

        if (rule is null)
            return Result.Fail<SimulateDispatchResult>("RULE_NOT_FOUND");

        var lead = await db.Leads
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<SimulateDispatchResult>("LEAD_NOT_FOUND");

        // Load available agents (same cross-module contract as real dispatch).
        var allCandidates = await usersModule.GetAvailableAgentsAsync(
            tenantId: lead.TenantId,
            agencyId: lead.PreferredAgencyId,
            ct: ct);

        // Apply exclusions.
        var excludedCount = rule.ExcludedAgentIds.Count > 0
            ? allCandidates.Count(a => rule.ExcludedAgentIds.Contains(a.Id))
            : 0;

        var candidates = rule.ExcludedAgentIds.Count > 0
            ? allCandidates.Where(a => !rule.ExcludedAgentIds.Contains(a.Id)).ToList()
            : allCandidates;

        // Score via the configured strategy (read-only — no side effects).
        var strategy = strategyFactory.Create(rule.Strategy);
        var scored   = await strategy.EvaluateAsync(lead, candidates, rule, scorer, ct);

        var ranked = scored
            .OrderByDescending(c => c.CompatibilityScore)
            .Select(c => new SimulatedCandidateDto(
                AgentId:                      c.Agent.Id,
                AgentName:                    c.Agent.FullName,
                CompatibilityScore:           c.CompatibilityScore,
                ActiveLeadsCount:             c.Agent.ActiveLeadsCount,
                WouldBeBlockedByAntiMonopoly: c.Agent.HotLeadsCount >= rule.AntiMonopolyThreshold))
            .ToList();

        return Result.Ok(new SimulateDispatchResult(
            RuleId:           rule.Id,
            RuleName:         rule.Name,
            Strategy:         rule.Strategy,
            LeadId:           lead.Id,
            RankedCandidates: ranked,
            ExcludedCount:    excludedCount));
    }
}
