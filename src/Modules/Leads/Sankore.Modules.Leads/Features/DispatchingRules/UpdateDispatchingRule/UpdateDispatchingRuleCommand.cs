namespace Sankore.Modules.Leads.Features.DispatchingRules.UpdateDispatchingRule;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateDispatchingRuleCommand(
    Guid RuleId,
    string Name,
    ScoringWeightsDto Weights,
    int MaxLeadsPerAgent,
    int MaxTasksPerAgent,
    int AntiMonopolyThreshold,
    TimeSpan FirstContactSla,
    int Priority = 0,
    IReadOnlyList<Guid>? ExcludedAgentIds = null,
    TimeSpan? DeclineExclusionTtl = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "DispatchingRule";
    public string? ResourceId  => RuleId.ToString();
}
