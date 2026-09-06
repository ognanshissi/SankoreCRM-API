using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.AssignPermissionToRole;

public sealed record AssignPermissionToRoleCommand(Guid RoleId, string PermissionCode)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Role";
    public string? ResourceId => RoleId.ToString();
}
