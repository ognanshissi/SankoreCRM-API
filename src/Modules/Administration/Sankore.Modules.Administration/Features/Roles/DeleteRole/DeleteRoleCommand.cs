using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.DeleteRole;

public sealed record DeleteRoleCommand(Guid RoleId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Role";
    public string? ResourceId => RoleId.ToString();
}
