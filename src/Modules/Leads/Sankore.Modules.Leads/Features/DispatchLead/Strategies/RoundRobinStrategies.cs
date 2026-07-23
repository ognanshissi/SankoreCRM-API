namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Users.PublicApi;

/// <summary>
/// Fair rotation across all available agents, ignoring compatibility
/// entirely. Simplicity over optimality — some tenants prefer pure equity
/// over algorithmic matching (F13.9).
///
/// "Round-robin" state (whose turn is next) is intentionally NOT stored on
/// this stateless strategy object: it is derived from ActiveLeadsCount,
/// the same signal already used for workload balance, so no separate
/// cursor/counter table is needed. The candidate with the fewest active
/// leads is treated as "next in line".
/// </summary>
internal sealed class RoundRobinStrategy: IDispatchingStrategy
{
    public Task<IReadOnlyList<ScoredCandidate>> EvaluateAsync(
        Lead lead,
        IReadOnlyList<AgentSummary> candidates,
        DispatchingRule rules,
        CompatibilityScorer scorer,
        CancellationToken ct)
    {
        // Rank purely by current load ascending; everyone eventually gets a turn.
        var scored = candidates
            .OrderBy(a => a.ActiveLeadsCount)
            .Select((agent, index) => new ScoredCandidate(
                agent,
                CompatibilityScore: 100 - index)) // preserves ranking order downstream
            .ToList();

        return Task.FromResult<IReadOnlyList<ScoredCandidate>>(scored);
    }
}

/// <summary>
/// Same idea as RoundRobinStrategy but senior/high-performing agents get a
/// larger effective share by weighting the load comparison with their
/// historical conversion rate (F13.9 "quotas").
/// </summary>
internal sealed class WeightedRoundRobinStrategy: IDispatchingStrategy
{
    public Task<IReadOnlyList<ScoredCandidate>> EvaluateAsync(
        Lead lead,
        IReadOnlyList<AgentSummary> candidates,
        DispatchingRule rules,
        CompatibilityScorer scorer,
        CancellationToken ct)
    {
        var scored = candidates
            .Select(agent =>
            {
                // A high performer "counts" their existing load as lighter,
                // so they receive proportionally more leads over time.
                var weightedLoad = agent.ActiveLeadsCount * (1 - agent.ConversionRate30d * 0.5);
                return new ScoredCandidate(agent, CompatibilityScore: -weightedLoad);
            })
            .OrderByDescending(c => c.CompatibilityScore) // least (negative) weighted load first
            .ToList();

        return Task.FromResult<IReadOnlyList<ScoredCandidate>>(scored);
    }
}
