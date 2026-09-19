namespace Sankore.Modules.Leads.Features.Tasks.AssignTask;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record AssignTaskCommand(Guid TaskId, Guid AgentId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => TaskId.ToString();
}
