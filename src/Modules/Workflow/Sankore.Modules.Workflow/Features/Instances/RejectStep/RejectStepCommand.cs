using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.RejectStep;

public sealed record RejectStepCommand(
    Guid InstanceId,
    string? Comment) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowInstance";
    public string? ResourceId => InstanceId.ToString();
}
