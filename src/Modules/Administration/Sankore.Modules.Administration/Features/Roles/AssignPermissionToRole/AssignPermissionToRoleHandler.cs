using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.AssignPermissionToRole;

internal sealed class AssignPermissionToRoleHandler(
    AdministrationDbContext db,
    RoleManager<AppRole> roleManager
) : IRequestHandler<AssignPermissionToRoleCommand, Result>
{
    public async Task<Result> Handle(AssignPermissionToRoleCommand request, CancellationToken ct)
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

        var alreadyGranted = await db.RolePermissions
            .AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id, ct);
        if (alreadyGranted)
            return Result.Fail("Permission is already granted to this role.");

        await db.RolePermissions.AddAsync(RolePermission.Grant(role.Id, permission.Id), ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
