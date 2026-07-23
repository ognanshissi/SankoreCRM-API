namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Users.PublicApi;

/// <summary>
/// A lead referred by an existing client is attributed to that client's
/// agent whenever possible (F13.9 "sticky assignment"). This scaffold
/// version falls back to CompatibilityScoring ranking among the remaining
/// candidates when no referring-agent hint is available on the lead —
/// in the full implementation the referring agent id would travel with
/// the lead's capture payload (F13.1) and be looked up here.
/// </summary>
internal sealed class StickyAssignmentStrategy(CompatibilityScoringStrategy fallback) : IDispatchingStrategy
{
    public async Task<IReadOnlyList<ScoredCandidate>> EvaluateAsync(
        Lead lead,
        IReadOnlyList<AgentSummary> candidates,
        DispatchingRule rules,
        CompatibilityScorer scorer,
        CancellationToken ct)
    {
        // TODO (post-MVP): resolve lead.ReferringAgentId once F13.1 captures it,
        // and short-circuit to that agent with CompatibilityScore = 100 if
        // they are present in `candidates` (i.e. still available).

        return await fallback.EvaluateAsync(lead, candidates, rules, scorer, ct);
    }
}

/// <summary>
/// Presents all Sales Qualified leads in a shared pool that agents pick
/// from themselves, capped at a configurable number of picks per day
/// (F13.9 "cherry-picking supervisé"). This strategy still RANKS
/// candidates by compatibility so the UI can highlight the best match,
/// but the actual selection authority belongs to the agent, not the
/// engine — DispatchLeadHandler is called only once an agent has picked,
/// with the chosen agent already known, so this strategy mainly serves
/// the "recommended candidates" read-model, not automatic assignment.
/// </summary>
internal sealed class CherryPickingStrategy(CompatibilityScoringStrategy fallback) : IDispatchingStrategy
{
    public Task<IReadOnlyList<ScoredCandidate>> EvaluateAsync(
        Lead lead,
        IReadOnlyList<AgentSummary> candidates,
        DispatchingRule rules,
        CompatibilityScorer scorer,
        CancellationToken ct)
        => fallback.EvaluateAsync(lead, candidates, rules, scorer, ct);
}
