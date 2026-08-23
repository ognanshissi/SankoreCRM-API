using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.RevokeRole;

internal sealed class RevokeRoleHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    ICurrentUser currentUser
) : IRequestHandler<RevokeRoleCommand, Result>
{
    public async Task<Result> Handle(RevokeRoleCommand request, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.TenantId != currentUser.TenantId)
            return Result.Fail("User not found.");

        var role = await roleManager.FindByIdAsync(request.RoleId.ToString());
        if (role is null)
            return Result.Fail("Role not found.");

        // Guard: cannot remove System role
        if (role.Name == global::Sankore.Shared.Kernel.Roles.System.Code)
            return Result.Fail("The System role cannot be revoked.");

        var userRole = await db.UserRoles
            .FirstOrDefaultAsync(ur => ur.UserId == request.UserId
                                    && ur.RoleId == request.RoleId
                                    && ur.IsActive, ct);
        if (userRole is null)
            return Result.Fail("User does not have this role.");

        var identityResult = await userManager.RemoveFromRoleAsync(user, role.Name!);
        if (!identityResult.Succeeded)
            return Result.Fail(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        userRole.Revoke();
        db.UserRoles.Update(userRole);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
