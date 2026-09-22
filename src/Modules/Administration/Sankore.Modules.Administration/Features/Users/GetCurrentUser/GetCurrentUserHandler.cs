namespace Sankore.Modules.Administration.Features.Users.GetCurrentUser;

using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class GetCurrentUserHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ICurrentUser currentUser)
    : IRequestHandler<GetCurrentUserQuery, Result<CurrentUserDto>>
{
    public async Task<Result<CurrentUserDto>> Handle(
        GetCurrentUserQuery request, CancellationToken ct)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);

        if (user is null)
            return Result.Fail<CurrentUserDto>("USER_NOT_FOUND");

        var roles = await userManager.GetRolesAsync(user);

        var roleIds = await db.Roles
            .Where(r => roles.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var rolePermissions = await db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

        var scopedPermissions = await db.PermissionAttributions
            .Where(pa => pa.UserId == user.Id && pa.IsActive)
            .Select(pa => pa.PermissionCode)
            .Distinct()
            .ToListAsync(ct);

        var allPermissions = rolePermissions
            .Union(scopedPermissions)
            .OrderBy(p => p)
            .ToList();

        var profile = await db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id, ct);

        return Result.Ok(new CurrentUserDto(
            Id:              user.Id,
            TenantId:        user.TenantId,
            Email:           user.Email!,
            FullName:        user.FullName,
            AgencyId:        user.AgencyId,
            IsSuperUser:     user.IsSuperUser,
            Status:          user.Status.ToString(),
            AccountType:     user.AccountType.ToString(),
            LastLoginAt:     user.LastLoginAt,
            Roles:           roles.ToList(),
            Permissions:     allPermissions,
            DefaultLanguage: profile?.DefaultLanguage ?? "fr"));
    }
}
