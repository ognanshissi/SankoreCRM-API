using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.CancelInstance;

public sealed record CancelInstanceCommand(Guid InstanceId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowInstance";
    public string? ResourceId => InstanceId.ToString();
}
