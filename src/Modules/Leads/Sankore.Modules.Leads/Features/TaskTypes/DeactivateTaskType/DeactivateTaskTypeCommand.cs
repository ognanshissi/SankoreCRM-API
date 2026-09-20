namespace Sankore.Modules.Leads.Features.TaskTypes.DeactivateTaskType;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DeactivateTaskTypeCommand(Guid TaskTypeId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskTypeConfig";
    public string? ResourceId  => TaskTypeId.ToString();
}
