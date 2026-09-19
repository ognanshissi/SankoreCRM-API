namespace Sankore.Modules.Leads.Features.Tasks.DeclineTask;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An agent declines an assigned task (US-M13-084).
/// The task returns to Pending, the agent is temporarily excluded from the
/// dispatch pool for this task (cached, TTL from DispatchingRule), and the
/// task is automatically re-dispatched.
/// </summary>
public sealed record DeclineTaskCommand(
    Guid TaskId,
    Guid AgentId,
    string Reason
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => TaskId.ToString();
}
