using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.ApproveStep;

public sealed record ApproveStepCommand(
    Guid InstanceId,
    string? Comment) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowInstance";
    public string? ResourceId => InstanceId.ToString();
}
