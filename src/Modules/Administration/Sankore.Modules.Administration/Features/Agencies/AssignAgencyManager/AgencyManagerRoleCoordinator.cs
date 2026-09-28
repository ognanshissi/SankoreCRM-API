using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

/// <summary>
/// Keeps the <c>BranchManager</c> role in step with who actually runs an agency.
///
/// The role has to go through <see cref="UserManager{TUser}"/>: login builds its claims from
/// <c>userManager.GetRolesAsync</c>, so a row written only into the custom <see cref="UserRole"/>
/// table would never reach a JWT. <see cref="UserRole"/> is the audit mirror and is written too,
/// exactly as <c>AssignRoleHandler</c> does.
/// </summary>
internal sealed class AgencyManagerRoleCoordinator(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager)
{
    /// <summary>
    /// Recorded as <see cref="UserRole.AssignedBy"/> for a grant this feature made by itself.
    ///
    /// It is what lets the revocation below stay conservative: a <c>BranchManager</c> role an
    /// administrator granted deliberately through <c>POST users/{id}/roles</c> must survive the
    /// person losing an agency, while a role this feature granted as a side effect must not
    /// outlive the reason it was granted. Who triggered the change is not lost — the pipeline's
    /// AuditBehavior records the acting administrator on the command itself.
    /// </summary>
    internal static readonly Guid SystemGrant = Guid.Empty;

    /// <summary>
    /// Gives <paramref name="manager"/> the BranchManager role unless they already hold it.
    /// Holding it already is left completely alone — including its original grantor — so a
    /// manual grant is never quietly converted into an automatic one.
    /// </summary>
    public async Task<Result> GrantAsync(AppUser manager, Guid tenantId, CancellationToken ct)
    {
        var role = await roleManager.FindByNameAsync(global::Sankore.Shared.Kernel.Roles.BranchManager.Code);
        if (role is null)
            return Result.Fail(AgencyManagerErrors.ManagerRoleMissing);

        var alreadyHeld = await db.UserRoles
            .AnyAsync(ur => ur.UserId == manager.Id && ur.RoleId == role.Id && ur.IsActive, ct);

        if (alreadyHeld)
            return Result.Ok();

        var identityResult = await userManager.AddToRoleAsync(manager, role.Name!);
        if (!identityResult.Succeeded)
            return Result.Fail(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        await db.UserRoles.AddAsync(UserRole.Assign(tenantId, manager.Id, role.Id, SystemGrant), ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }

    /// <summary>
    /// Takes the BranchManager role back from an outgoing manager, but only when both hold:
    /// they no longer manage ANY agency in the tenant (someone can run two branches), and the
    /// active grant was automatic. Call this AFTER the agency change has been saved, so the
    /// "still manages something" query sees the new reality.
    /// </summary>
    public async Task<Result> RevokeIfVacatedAsync(Guid outgoingManagerId, Guid tenantId, CancellationToken ct)
    {
        var stillManagesSomething = await db.Agencies
            .AnyAsync(a => a.ManagerUserId == outgoingManagerId && !a.IsDeleted, ct);

        if (stillManagesSomething)
            return Result.Ok();

        var role = await roleManager.FindByNameAsync(global::Sankore.Shared.Kernel.Roles.BranchManager.Code);
        if (role is null)
            return Result.Ok();

        var grant = await db.UserRoles
            .AsTracking()
            .FirstOrDefaultAsync(
                ur => ur.UserId == outgoingManagerId && ur.RoleId == role.Id && ur.IsActive, ct);

        // No active grant, or one an administrator made on purpose: leave it be.
        if (grant is null || grant.AssignedBy != SystemGrant)
            return Result.Ok();

        var outgoing = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == outgoingManagerId, ct);
        if (outgoing is null)
            return Result.Ok();

        var identityResult = await userManager.RemoveFromRoleAsync(outgoing, role.Name!);
        if (!identityResult.Succeeded)
            return Result.Fail(string.Join("; ", identityResult.Errors.Select(e => e.Description)));

        grant.Revoke();
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
