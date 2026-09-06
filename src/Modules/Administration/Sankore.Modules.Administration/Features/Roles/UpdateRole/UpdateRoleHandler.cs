using MediatR;
using Microsoft.AspNetCore.Identity;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.UpdateRole;

internal sealed class UpdateRoleHandler(RoleManager<AppRole> roleManager)
    : IRequestHandler<UpdateRoleCommand, Result>
{
    public async Task<Result> Handle(UpdateRoleCommand request, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(request.RoleId.ToString());
        if (role is null)
            return Result.Fail("Role not found.");

        if (role.IsSystem)
            return Result.Fail("SYSTEM_ROLE: System roles cannot be modified.");

        role.UpdateLabel(request.Label);
        var identityResult = await roleManager.UpdateAsync(role);
        if (!identityResult.Succeeded)
            return Result.Fail(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        return Result.Ok();
    }
}
