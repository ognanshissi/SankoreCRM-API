using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserPermissions;

public sealed record GetUserPermissionsQuery(Guid UserId)
    : IRequest<Result<UserPermissionsDto>>;

public sealed record UserPermissionsDto(
    Guid UserId,
    List<string> RoleNames,
    List<string> RolePermissionCodes,
    List<ScopedPermissionDto> ScopedPermissions
);

public sealed record ScopedPermissionDto(
    Guid Id,
    string PermissionCode,
    Guid? ScopeId,
    string? ScopeType,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate
);
