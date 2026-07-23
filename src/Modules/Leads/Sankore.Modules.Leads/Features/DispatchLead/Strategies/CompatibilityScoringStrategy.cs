namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Users.PublicApi;

/// <summary>
/// Default and recommended strategy (F13.10): scores every candidate via
/// CompatibilityScorer and returns them ranked. The handler applies the
/// anti-monopoly filter afterward and picks the top eligible candidate.
/// </summary>
internal sealed class CompatibilityScoringStrategy : IDispatchingStrategy
{
    public Task<IReadOnlyList<ScoredCandidate>> EvaluateAsync(
        Lead lead,
        IReadOnlyList<AgentSummary> candidates,
        DispatchingRule rules,
        CompatibilityScorer scorer,
        CancellationToken ct)
    {
        var scored = candidates
            .Select(agent => new ScoredCandidate(agent, scorer.Score(lead, agent, rules)))
            .OrderByDescending(c => c.CompatibilityScore)
            .ToList();

        return Task.FromResult<IReadOnlyList<ScoredCandidate>>(scored);
    }
}
