namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// A single scored candidate produced by a strategy. Higher CompatibilityScore
/// wins, subject to the anti-monopoly filter applied afterward by the handler.
/// FactorsJson carries the per-factor breakdown for dispatch audit (US-M13-072).
/// </summary>
public sealed record ScoredCandidate(AgentSummary Agent, double CompatibilityScore, string FactorsJson = "{}");

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
