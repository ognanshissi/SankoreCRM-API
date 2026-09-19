namespace Sankore.Modules.Leads.Features.DispatchingRules.SimulateDispatch;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>Dry-run dispatch — computes ranked candidates without persisting anything.</summary>
internal sealed record SimulateDispatchQuery(Guid RuleId, Guid LeadId)
    : IRequest<Result<SimulateDispatchResult>>;

public sealed record SimulateDispatchResult(
    Guid RuleId,
    string RuleName,
    DispatchingStrategy Strategy,
    Guid LeadId,
    IReadOnlyList<SimulatedCandidateDto> RankedCandidates,
    /// <summary>Agents excluded by the rule's ExcludedAgentIds list.</summary>
    int ExcludedCount);

public sealed record SimulatedCandidateDto(
    Guid AgentId,
    string AgentName,
    double CompatibilityScore,
    int ActiveLeadsCount,
    bool WouldBeBlockedByAntiMonopoly);
