namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Users.PublicApi;

/// <summary>
/// A single scored candidate produced by a strategy. Higher CompatibilityScore
/// wins, subject to the anti-monopoly filter applied afterward by the handler.
/// </summary>
public sealed record ScoredCandidate(AgentSummary Agent, double CompatibilityScore);

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
