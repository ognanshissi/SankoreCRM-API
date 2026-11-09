using MediatR;
using Sankore.Modules.Administration.Features.PermissionsCatalog.ListPermissions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.PermissionsCatalog.ListPermissions;

internal sealed class ListPermissionsHandler(ITenantModules? modules = null, ITenantContext? tenant = null)
    : IRequestHandler<ListPermissionsQuery, Result<List<PermissionGroupDto>>>
{
    public async Task<Result<List<PermissionGroupDto>>> Handle(
        ListPermissionsQuery request, CancellationToken ct)
    {
        var source = Shared.Kernel.Permissions.All.AsEnumerable();

        // A paid module's permissions are offered only where the module is in reach (docs/isolation-socle.md, lot 2).
        if (modules is not null && tenant is not null)
        {
            var readable = await modules.ReadableSetAsync(tenant.CurrentTenantId, ct);
            source = source.Where(p => ModuleScopes.InReach(ModuleScopes.OfPermission(p.Code), readable));
        }

        if (!string.IsNullOrWhiteSpace(request.Module))
            source = source.Where(p =>
                p.Module.Equals(request.Module, StringComparison.OrdinalIgnoreCase));

        var groups = source
            .GroupBy(p => p.Module)
            .OrderBy(g => g.Key)
            .Select(g => new PermissionGroupDto(
                g.Key,
                g.OrderBy(p => p.Action)
                 .Select(p => new PermissionDto(p.Code, p.Description, p.Action))
                 .ToList()))
            .ToList();

        return Result.Ok(groups);
    }
}
