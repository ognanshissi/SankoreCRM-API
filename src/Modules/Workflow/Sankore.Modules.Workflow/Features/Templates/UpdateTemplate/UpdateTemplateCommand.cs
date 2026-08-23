using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.UpdateTemplate;

public sealed record UpdateTemplateCommand(
    Guid TemplateId,
    string Name,
    string? Description) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId => TemplateId.ToString();
}
