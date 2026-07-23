namespace Sankore.Modules.Leads.Features.DispatchLead.Strategies;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// Resolves the concrete IDispatchingStrategy for a given
/// DispatchingStrategy enum value. Registered as a factory (not a
/// dictionary of singletons) so each strategy can have its own scoped
/// dependencies if needed later (e.g. StickyAssignmentStrategy eventually
/// querying a referral lookup service).
/// </summary>
internal sealed class DispatchingStrategyFactory(
    CompatibilityScoringStrategy compatibilityScoring,
    RoundRobinStrategy roundRobin,
    WeightedRoundRobinStrategy weightedRoundRobin,
    StickyAssignmentStrategy stickyAssignment,
    CherryPickingStrategy cherryPicking)
{
    public IDispatchingStrategy Create(DispatchingStrategy strategy) => strategy switch
    {
        DispatchingStrategy.CompatibilityScoring => compatibilityScoring,
        DispatchingStrategy.RoundRobin => roundRobin,
        DispatchingStrategy.WeightedRoundRobin => weightedRoundRobin,
        DispatchingStrategy.StickyAssignment => stickyAssignment,
        DispatchingStrategy.CherryPicking => cherryPicking,
        _ => throw new ArgumentOutOfRangeException(
            nameof(strategy), strategy, "Unknown dispatching strategy.")
    };
}
