using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.AddStep;

public sealed record AddStepCommand(
    Guid TemplateId,
    int Order,
    string Name,
    string? Description,
    string? ApproverRoleCode,
    int? TimeoutHours) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId => TemplateId.ToString();
}
