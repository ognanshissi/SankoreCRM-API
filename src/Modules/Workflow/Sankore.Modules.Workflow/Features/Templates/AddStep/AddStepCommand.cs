using MediatR;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.AddStep;

public sealed record AddStepCommand(
    Guid TemplateId,
    int Order,
    string Name,
    string? Description,
    string? ApproverRoleCode,
    int? TimeoutHours,
    StepKind Kind = StepKind.Validation,
    string? ConfigJson = null,
    bool IsBlocking = false,
    bool InsertAt = false) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowTemplate";
    public string? ResourceId => TemplateId.ToString();
}
