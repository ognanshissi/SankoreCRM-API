using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.GetRole;

public sealed record GetRoleQuery(Guid RoleId) : IRequest<Result<RoleDetailDto>>;

public sealed record RoleDetailDto(
    Guid Id,
    string Name,
    string Label,
    bool IsSystem,
    bool IsAssignable,
    Guid? TenantId,
    List<RolePermissionDto> Permissions
);

public sealed record RolePermissionDto(Guid PermissionId, string Code, string Description);
