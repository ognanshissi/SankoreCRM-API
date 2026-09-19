namespace Sankore.Modules.Leads.Features.Tasks.StartTask;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record StartTaskCommand(Guid TaskId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId  => TaskId.ToString();
}
