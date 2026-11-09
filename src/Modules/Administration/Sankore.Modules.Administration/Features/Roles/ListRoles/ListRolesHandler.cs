using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.ListRoles;

internal sealed class ListRolesHandler(AdministrationDbContext db, ICurrentUser currentUser, ITenantModules? modules = null)
    : IRequestHandler<ListRolesQuery, Result<List<RoleDto>>>
{
    public async Task<Result<List<RoleDto>>> Handle(ListRolesQuery request, CancellationToken ct)
    {
        var roles = await db.Roles
            .AsNoTracking()
            // System roles (TenantId == null) are visible to all tenants.
            // Custom roles are scoped to the calling tenant.
            .Where(r => r.IsAssignable
                     && (r.TenantId == null || r.TenantId == currentUser.TeamTenantId))
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(
                r.Id,
                r.Name!,
                r.Label,
                r.IsSystem,
                r.IsAssignable,
                db.RolePermissions.Count(rp => rp.RoleId == r.Id)))
            .ToListAsync(ct);

        // Built-in roles of a paid module, and the module's permissions in each count, only where
        // the module is in reach (docs/isolation-socle.md, lot 2). Its holders keep the role.
        if (modules is not null)
        {
            var readable = await modules.ReadableSetAsync(currentUser.TenantId, ct);
            roles = roles.Where(r => ModuleScopes.InReach(ModuleScopes.OfRole(r.Name), readable)).ToList();

            var ids = roles.Select(r => r.Id).ToList();
            var grants = await db.RolePermissions.AsNoTracking()
                .Where(rp => ids.Contains(rp.RoleId))
                .Select(rp => new { rp.RoleId, rp.Permission.Code })
                .ToListAsync(ct);
            var counts = grants
                .Where(g => ModuleScopes.InReach(ModuleScopes.OfPermission(g.Code), readable))
                .GroupBy(g => g.RoleId)
                .ToDictionary(g => g.Key, g => g.Count());
            roles = roles.Select(r => r with { PermissionCount = counts.GetValueOrDefault(r.Id) }).ToList();
        }

        return Result.Ok(roles);
    }
}
