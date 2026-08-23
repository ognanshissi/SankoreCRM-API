using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignRole;

public sealed record AssignRoleCommand(Guid UserId, Guid RoleId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}
