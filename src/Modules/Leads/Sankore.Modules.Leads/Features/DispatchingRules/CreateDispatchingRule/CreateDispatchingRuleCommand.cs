namespace Sankore.Modules.Leads.Features.DispatchingRules.CreateDispatchingRule;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateDispatchingRuleCommand(
    Guid TenantId,
    string Name,
    DispatchingStrategy Strategy,
    ScoringWeightsDto Weights,
    int MaxLeadsPerAgent,
    int AntiMonopolyThreshold,
    TimeSpan FirstContactSla,
    int Priority = 0,
    IReadOnlyList<Guid>? ExcludedAgentIds = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "DispatchingRule";
    public string? ResourceId  => null;
}
