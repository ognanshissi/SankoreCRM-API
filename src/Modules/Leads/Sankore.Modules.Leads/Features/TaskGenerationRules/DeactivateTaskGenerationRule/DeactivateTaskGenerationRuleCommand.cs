namespace Sankore.Modules.Leads.Features.TaskGenerationRules.DeactivateTaskGenerationRule;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DeactivateTaskGenerationRuleCommand(Guid RuleId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskGenerationRule";
    public string? ResourceId  => RuleId.ToString();
}
