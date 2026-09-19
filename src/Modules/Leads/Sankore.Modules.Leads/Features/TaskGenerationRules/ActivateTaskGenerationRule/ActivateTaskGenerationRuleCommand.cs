namespace Sankore.Modules.Leads.Features.TaskGenerationRules.ActivateTaskGenerationRule;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ActivateTaskGenerationRuleCommand(Guid RuleId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskGenerationRule";
    public string? ResourceId  => RuleId.ToString();
}
