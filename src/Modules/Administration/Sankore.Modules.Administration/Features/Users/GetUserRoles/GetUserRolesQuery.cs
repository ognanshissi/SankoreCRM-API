using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserRoles;

public sealed record GetUserRolesQuery(Guid UserId) : IRequest<Result<List<UserRoleDto>>>;

public sealed record UserRoleDto(
    Guid Id,
    string Name,
    string Label,
    bool IsSystem,
    DateTimeOffset AssignedAt);
