namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// A single scored candidate produced by a strategy. Higher CompatibilityScore
/// wins, subject to the anti-monopoly filter applied afterward by the handler.
/// </summary>
/// <param name="Factors">
/// The per-factor breakdown behind the score, for dispatch audit (US-M13-072) and for the
/// dispatch preview. NULL for the strategies that rank by load alone — round-robin and weighted
/// round-robin compute no breakdown, and inventing an empty one would read as "every factor
/// scored zero" rather than "not applicable".
/// </param>
public sealed record ScoredCandidate(
    AgentSummary Agent, double CompatibilityScore, CompatibilityFactors? Factors = null);

/// <summary>
/// Strategy abstraction (F13.9): each dispatching strategy configured by a
/// tenant implements this interface. DispatchingStrategyFactory selects the
/// right implementation at runtime based on DispatchingRule.Strategy.
/// </summary>
internal interface IDispatchingStrategy
{
    Task<IReadOnlyList<ScoredCandidate>> EvaluateAsync(
        Lead lead,
        IReadOnlyList<AgentSummary> candidates,
        DispatchingRule rules,
        CompatibilityScorer scorer,
        CancellationToken ct);
}
