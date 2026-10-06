namespace Sankore.Modules.Leads.Features.DispatchLead.PreviewDispatch;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead.Strategies;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Runs the same selection the dispatcher would and reports it, instead of acting on it.
///
/// It mirrors <see cref="DispatchLeadHandler"/>'s steps 2 to 5 — candidate pool, applicable rule,
/// exclusions, task capacity, strategy scoring, anti-monopoly — with two deliberate differences:
///
/// <list type="bullet">
/// <item>nothing is written and nothing is published. The dispatcher emits
///   <c>LeadDispatchingFailedEvent</c> and <c>AntiMonopolyTriggeredEvent</c> when it cannot place
///   a lead; a preview that did that would raise alerts for a screen someone merely opened.</item>
/// <item>blocked candidates are KEPT, each carrying what blocks it. The dispatcher filters them
///   away because it only needs a winner; a human choosing an agent needs to see that someone is
///   saturated or excluded, which is exactly what the reassignment screen already renders.</item>
/// </list>
///
/// The duplication of the filter chain is accepted rather than extracted: the two have different
/// obligations (one must stop at the first viable winner and emit events, the other must describe
/// every candidate and emit none), and folding them into one method behind flags would make the
/// dispatcher's behaviour depend on a caller-supplied mode. If they drift, the tests pinning
/// "preview agrees with dispatch" are what should catch it.
/// </summary>
internal sealed class PreviewDispatchHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule usersModule,
    CompatibilityScorer scorer,
    DispatchingStrategyFactory strategyFactory,
    DispatchingRuleResolver ruleResolver,
    AgentCapacityService capacityService)
    : IRequestHandler<PreviewDispatchQuery, Result<DispatchPreviewResult>>
{
    public async Task<Result<DispatchPreviewResult>> Handle(
        PreviewDispatchQuery query, CancellationToken ct)
    {
        var tenantId = tenant.CurrentTenantId;

        var lead = await db.Leads
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<DispatchPreviewResult>("LEAD_NOT_FOUND");

        // A closed lead has no dispatch to preview. Reported as a failure rather than an empty
        // list so the caller does not read "no agents available" into a terminal status.
        if (!lead.IsDispatchable)
            return Result.Fail<DispatchPreviewResult>("LEAD_NOT_DISPATCHABLE");

        var rules = await ruleResolver.ResolveAsync(lead, requestedStrategy: null, ct);

        var candidates = await usersModule.GetAvailableAgentsAsync(
            tenantId: tenantId,
            agencyId: lead.PreferredAgencyId,
            ct: ct);

        if (candidates.Count == 0)
        {
            return Result.Ok(new DispatchPreviewResult(
                LeadId: lead.Id,
                Strategy: rules.Strategy,
                RuleId: rules.Id == Guid.Empty ? null : rules.Id,
                RuleName: rules.Id == Guid.Empty ? null : rules.Name,
                MaxTasksPerAgent: rules.MaxTasksPerAgent,
                AntiMonopolyThreshold: rules.AntiMonopolyThreshold,
                WouldAssignToAgentId: null,
                Candidates: []));
        }

        // Score EVERY candidate, including the ones a dispatch would have filtered out first.
        // The strategy is given the whole pool on purpose: a round-robin or sticky strategy
        // ranks relative to what it is shown, so pre-filtering here would report scores that a
        // real dispatch never computed.
        var strategy = strategyFactory.Create(rules.Strategy);
        var scored = await strategy.EvaluateAsync(lead, candidates, rules, scorer, ct);

        var openTaskCounts = new Dictionary<Guid, int>(scored.Count);
        foreach (var candidate in scored)
        {
            openTaskCounts[candidate.Agent.Id] =
                await capacityService.GetOpenTaskCountAsync(tenantId, candidate.Agent.Id, ct);
        }

        var rows = scored
            .Select(c =>
            {
                var excluded = rules.ExcludedAgentIds.Contains(c.Agent.Id);
                var openTasks = openTaskCounts[c.Agent.Id];
                var atCapacity = openTasks >= rules.MaxTasksPerAgent;
                var monopoly = c.Agent.HotLeadsCount >= rules.AntiMonopolyThreshold;

                return new DispatchPreviewCandidate(
                    AgentId: c.Agent.Id,
                    FullName: c.Agent.FullName,
                    AgencyId: c.Agent.AgencyId,
                    CompatibilityScore: c.CompatibilityScore,
                    Factors: c.Factors,
                    OpenTaskCount: openTasks,
                    HotLeadsCount: c.Agent.HotLeadsCount,
                    IsExcludedByRule: excluded,
                    IsAtTaskCapacity: atCapacity,
                    IsBlockedByAntiMonopoly: monopoly,
                    IsEligible: !excluded && !atCapacity && !monopoly);
            })
            .OrderByDescending(c => c.IsEligible)
            .ThenByDescending(c => c.CompatibilityScore)
            .ToList();

        // The same winner the dispatcher would land on: highest score among those that clear
        // every filter. Null is a real answer here — it says "nobody is dispatchable right now",
        // and the per-candidate flags say why.
        var wouldWin = rows.FirstOrDefault(c => c.IsEligible);

        return Result.Ok(new DispatchPreviewResult(
            LeadId: lead.Id,
            Strategy: rules.Strategy,
            RuleId: rules.Id == Guid.Empty ? null : rules.Id,
            RuleName: rules.Id == Guid.Empty ? null : rules.Name,
            MaxTasksPerAgent: rules.MaxTasksPerAgent,
            AntiMonopolyThreshold: rules.AntiMonopolyThreshold,
            WouldAssignToAgentId: wouldWin?.AgentId,
            Candidates: rows));
    }
}
