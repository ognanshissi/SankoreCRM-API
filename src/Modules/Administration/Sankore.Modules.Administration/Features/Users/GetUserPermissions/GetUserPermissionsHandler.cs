using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserPermissions;

internal sealed class GetUserPermissionsHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ICurrentUser currentUser
) : IRequestHandler<GetUserPermissionsQuery, Result<UserPermissionsDto>>
{
    public async Task<Result<UserPermissionsDto>> Handle(
        GetUserPermissionsQuery request, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.TenantId != currentUser.TenantId)
            return Result.Fail<UserPermissionsDto>("User not found.");

        // 1. Role names from Identity
        var roleNames = (await userManager.GetRolesAsync(user)).ToList();

        // 2. Permission codes granted via roles
        var rolePermissionCodes = await db.Roles
            .Where(r => roleNames.Contains(r.Name!))
            .Join(db.RolePermissions, r => r.Id, rp => rp.RoleId, (_, rp) => rp.PermissionId)
            .Join(db.Permissions, permId => permId, p => p.Id, (_, p) => p.Code)
            .Distinct()
            .ToListAsync(ct);

        // 3. Active, non-expired direct attributions (scoped or global)
        var now = DateTimeOffset.UtcNow;
        var scopedPermissions = await db.PermissionAttributions
            .Where(a => a.UserId == request.UserId && a.IsActive && a.EndDate > now)
            .Select(a => new ScopedPermissionDto(a.Id, a.PermissionCode, a.ScopeId, a.ScopeType, a.StartDate, a.EndDate))
            .ToListAsync(ct);

        return Result.Ok(new UserPermissionsDto(
            request.UserId,
            roleNames,
            rolePermissionCodes,
            scopedPermissions));
    }
}
