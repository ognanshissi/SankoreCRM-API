using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.PermissionsCatalog.ListPermissions;

public sealed record ListPermissionsQuery(string? Module = null)
    : IRequest<Result<List<PermissionGroupDto>>>;

public sealed record PermissionGroupDto(
    string Module,
    List<PermissionDto> Permissions);

public sealed record PermissionDto(
    string Code,
    string Description,
    string Action);
