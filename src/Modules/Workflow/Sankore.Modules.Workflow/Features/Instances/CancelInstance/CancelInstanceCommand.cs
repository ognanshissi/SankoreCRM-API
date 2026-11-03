using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Instances.CancelInstance;

/// <param name="Reason">Why it stops — written in the instance history (« Santé des workflows », 2026-11-03).</param>
public sealed record CancelInstanceCommand(Guid InstanceId, string? Reason = null)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WorkflowInstance";
    public string? ResourceId => InstanceId.ToString();
}
