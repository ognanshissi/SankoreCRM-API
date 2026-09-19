namespace Sankore.Modules.Leads.Features.Tasks.CompleteTask;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CompleteTaskCommand(Guid TaskId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => TaskId.ToString();
}
