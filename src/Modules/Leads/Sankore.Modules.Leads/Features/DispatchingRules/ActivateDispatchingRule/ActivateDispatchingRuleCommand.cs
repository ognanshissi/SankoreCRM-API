namespace Sankore.Modules.Leads.Features.DispatchingRules.ActivateDispatchingRule;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ActivateDispatchingRuleCommand(Guid RuleId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "DispatchingRule";
    public string? ResourceId  => RuleId.ToString();
}
