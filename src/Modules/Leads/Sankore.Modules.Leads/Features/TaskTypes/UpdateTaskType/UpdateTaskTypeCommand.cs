namespace Sankore.Modules.Leads.Features.TaskTypes.UpdateTaskType;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateTaskTypeCommand(
    Guid TaskTypeId,
    string Label,
    string? Description,
    int DisplayOrder
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskTypeConfig";
    public string? ResourceId  => TaskTypeId.ToString();
}
