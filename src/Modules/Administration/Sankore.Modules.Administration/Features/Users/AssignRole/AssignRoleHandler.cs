using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignRole;

internal sealed class AssignRoleHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    ICurrentUser currentUser
) : IRequestHandler<AssignRoleCommand, Result>
{
    public async Task<Result> Handle(AssignRoleCommand request, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.TenantId != currentUser.TenantId)
            return Result.Fail("User not found.");

        var role = await roleManager.FindByIdAsync(request.RoleId.ToString());
        if (role is null || !role.IsAssignable)
            return Result.Fail("Role not found or not assignable.");

        // Guard: already has this role active
        var alreadyAssigned = await db.UserRoles
            .AnyAsync(ur => ur.UserId == request.UserId
                         && ur.RoleId == request.RoleId
                         && ur.IsActive, ct);
        if (alreadyAssigned)
            return Result.Fail("User already has this role.");

        var identityResult = await userManager.AddToRoleAsync(user, role.Name!);
        if (!identityResult.Succeeded)
            return Result.Fail(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        var userRole = UserRole.Assign(currentUser.TenantId, user.Id, role.Id, currentUser.Id);
        await db.UserRoles.AddAsync(userRole, ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
