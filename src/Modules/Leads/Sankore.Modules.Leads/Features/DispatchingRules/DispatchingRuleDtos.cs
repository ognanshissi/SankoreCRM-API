namespace Sankore.Modules.Leads.Features.DispatchingRules;

using Sankore.Modules.Leads.Domain;

public sealed record DispatchingRuleDto(
    Guid Id,
    string Name,
    DispatchingStrategy Strategy,
    ScoringWeightsDto Weights,
    int MaxLeadsPerAgent,
    int MaxTasksPerAgent,
    int AntiMonopolyThreshold,
    TimeSpan FirstContactSla,
    TimeSpan DeclineExclusionTtl,
    bool IsActive,
    int Priority,
    IReadOnlyList<Guid> ExcludedAgentIds,
    TimeSpan? FirstContactSlaHot = null,
    TimeSpan? FirstContactSlaWarm = null,
    TimeSpan? FirstContactSlaCold = null,
    /// <summary>A rule for the key accounts only (key accounts lot 4).</summary>
    bool KeyAccountsOnly = false,
    /// <summary>The agents the rule dispatches to; empty = every available agent.</summary>
    IReadOnlyList<Guid>? ReservedAgentIds = null);

public sealed record ScoringWeightsDto(
    double Language,
    double Product,
    double Geography,
    double Workload,
    double Performance,
    double Agency = 0);
