using MediatR;
using Microsoft.AspNetCore.Identity;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.DeleteRole;

internal sealed class DeleteRoleHandler(
    RoleManager<AppRole> roleManager,
    UserManager<AppUser> userManager
) : IRequestHandler<DeleteRoleCommand, Result>
{
    public async Task<Result> Handle(DeleteRoleCommand request, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(request.RoleId.ToString());
        if (role is null)
            return Result.Fail("Role not found.");

        if (role.IsSystem)
            return Result.Fail("SYSTEM_ROLE: System roles cannot be deleted.");

        var usersInRole = await userManager.GetUsersInRoleAsync(role.Name!);
        if (usersInRole.Count > 0)
            return Result.Fail("ROLE_HAS_USERS: Cannot delete a role that is still assigned to users.");

        var identityResult = await roleManager.DeleteAsync(role);
        if (!identityResult.Succeeded)
            return Result.Fail(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        return Result.Ok();
    }
}
