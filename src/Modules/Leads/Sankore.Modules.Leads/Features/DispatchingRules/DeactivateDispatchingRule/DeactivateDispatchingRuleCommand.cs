namespace Sankore.Modules.Leads.Features.DispatchingRules.DeactivateDispatchingRule;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DeactivateDispatchingRuleCommand(Guid RuleId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "DispatchingRule";
    public string? ResourceId  => RuleId.ToString();
}
