using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.RevokeScopedPermission;

public sealed record RevokeScopedPermissionCommand(Guid UserId, Guid AttributionId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "User";
    public string? ResourceId => UserId.ToString();
}
