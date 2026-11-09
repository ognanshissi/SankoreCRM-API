using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.GetRole;

internal sealed class GetRoleHandler(AdministrationDbContext db, ITenantModules? modules = null, ITenantContext? tenant = null)
    : IRequestHandler<GetRoleQuery, Result<RoleDetailDto>>
{
    public async Task<Result<RoleDetailDto>> Handle(GetRoleQuery request, CancellationToken ct)
    {
        var role = await db.Roles
            .AsNoTracking()
            .Where(r => r.Id == request.RoleId)
            .Select(r => new { r.Id, r.Name, r.Label, r.IsSystem, r.IsAssignable, r.TenantId })
            .FirstOrDefaultAsync(ct);

        if (role is null)
            return Result.Fail<RoleDetailDto>("Role not found.");

        var permissions = await db.RolePermissions
            .AsNoTracking()
            .Where(rp => rp.RoleId == request.RoleId)
            .Select(rp => new RolePermissionDto(rp.PermissionId, rp.Permission.Code, rp.Permission.Description))
            .ToListAsync(ct);

        if (modules is not null && tenant is not null)
        {
            var readable = await modules.ReadableSetAsync(tenant.CurrentTenantId, ct);
            permissions = permissions.Where(p => ModuleScopes.InReach(ModuleScopes.OfPermission(p.Code), readable)).ToList();
        }

        return Result.Ok(new RoleDetailDto(
            role.Id, role.Name!, role.Label, role.IsSystem, role.IsAssignable,
            role.TenantId, permissions));
    }
}
