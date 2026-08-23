using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.RemoveStep;

public sealed record RemoveStepCommand(Guid TemplateId, Guid StepId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId => TemplateId.ToString();
}
