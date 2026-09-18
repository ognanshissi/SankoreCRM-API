namespace Sankore.Modules.Leads.Features.DispatchingRules.UpdateDispatchingRule;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateDispatchingRuleCommand(
    Guid RuleId,
    string Name,
    ScoringWeightsDto Weights,
    int MaxLeadsPerAgent,
    int AntiMonopolyThreshold,
    TimeSpan FirstContactSla
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "DispatchingRule";
    public string? ResourceId  => RuleId.ToString();
}
