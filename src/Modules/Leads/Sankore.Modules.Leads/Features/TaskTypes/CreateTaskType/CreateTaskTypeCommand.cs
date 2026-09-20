namespace Sankore.Modules.Leads.Features.TaskTypes.CreateTaskType;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateTaskTypeCommand(
    Guid TenantId,
    string Code,
    string Label,
    string? Description,
    int DisplayOrder
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "TaskTypeConfig";
    public string? ResourceId  => null;
}
