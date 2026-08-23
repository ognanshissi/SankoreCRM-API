using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignScopedPermission;

public sealed record AssignScopedPermissionCommand(
    Guid UserId,
    string PermissionCode,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    Guid? ScopeId = null,
    string? ScopeType = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}
