namespace Sankore.Modules.Leads.Features.TaskTypes.ActivateTaskType;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ActivateTaskTypeCommand(Guid TaskTypeId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskTypeConfig";
    public string? ResourceId  => TaskTypeId.ToString();
}
