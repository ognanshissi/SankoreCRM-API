using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.RemoveRule;

public sealed record RemoveRuleCommand(Guid TemplateId, Guid StepId, Guid RuleId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId  => TemplateId.ToString();
}
