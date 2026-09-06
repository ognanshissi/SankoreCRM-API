using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.UpdateRole;

public sealed record UpdateRoleCommand(Guid RoleId, string Label)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Role";
    public string? ResourceId => RoleId.ToString();
}
