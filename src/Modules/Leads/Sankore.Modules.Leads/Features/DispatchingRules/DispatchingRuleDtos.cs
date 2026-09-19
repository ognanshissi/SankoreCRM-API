namespace Sankore.Modules.Leads.Features.DispatchingRules;

using Sankore.Modules.Leads.Domain;

public sealed record DispatchingRuleDto(
    Guid Id,
    string Name,
    DispatchingStrategy Strategy,
    ScoringWeightsDto Weights,
    int MaxLeadsPerAgent,
    int AntiMonopolyThreshold,
    TimeSpan FirstContactSla,
    bool IsActive,
    int Priority,
    IReadOnlyList<Guid> ExcludedAgentIds);

public sealed record ScoringWeightsDto(
    double Language,
    double Product,
    double Geography,
    double Workload,
    double Performance,
    double Agency = 0);
