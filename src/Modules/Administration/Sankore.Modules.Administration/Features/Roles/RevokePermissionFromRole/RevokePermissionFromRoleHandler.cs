using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.RevokePermissionFromRole;

internal sealed class RevokePermissionFromRoleHandler(
    AdministrationDbContext db,
    RoleManager<AppRole> roleManager
) : IRequestHandler<RevokePermissionFromRoleCommand, Result>
{
    public async Task<Result> Handle(RevokePermissionFromRoleCommand request, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(request.RoleId.ToString());
        if (role is null)
            return Result.Fail("Role not found.");

        if (role.IsSystem)
            return Result.Fail("SYSTEM_ROLE: Cannot modify permissions on system roles.");

        var permission = await db.Permissions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == request.PermissionCode, ct);
        if (permission is null)
            return Result.Fail("Permission not found.");

        var rolePermission = await db.RolePermissions
            .FirstOrDefaultAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id, ct);
        if (rolePermission is null)
            return Result.Fail("Permission is not granted to this role.");

        db.RolePermissions.Remove(rolePermission);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
