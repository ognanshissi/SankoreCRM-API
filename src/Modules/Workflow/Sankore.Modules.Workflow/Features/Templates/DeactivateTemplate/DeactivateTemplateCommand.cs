using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.DeactivateTemplate;

public sealed record DeactivateTemplateCommand(Guid TemplateId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId => TemplateId.ToString();
}
