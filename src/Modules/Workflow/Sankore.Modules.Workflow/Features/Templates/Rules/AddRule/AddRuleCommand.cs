using MediatR;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.Rules.AddRule;

public sealed record AddRuleCommand(
    Guid TemplateId,
    Guid StepId,
    RuleType RuleType,
    string Field,
    RuleOperator Operator,
    string Value,
    int LogicalGroup = 0
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId  => TemplateId.ToString();
}
