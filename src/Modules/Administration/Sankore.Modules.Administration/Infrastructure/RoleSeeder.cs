using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Infrastructure;

/// <summary>
/// Seeds the fixed set of system roles into the identity database on startup.
/// Idempotent — safe to run on every boot. System and Administrator roles
/// always receive ALL permissions, including any newly added ones.
/// </summary>
internal static class RoleSeeder
{
    internal static async Task SeedAsync(IServiceProvider sp)
    {
        var roleManager = sp.GetRequiredService<RoleManager<AppRole>>();
        var logger = sp.GetRequiredService<ILogger<AdministrationDbContext>>();
        var db = sp.GetRequiredService<AdministrationDbContext>();

        // 1. Seed permissions from Permissions.All
        await PermissionSeeder.SeedAsync(sp);

        // 2. Ensure all roles exist
        foreach (RoleItem role in Roles.All)
        {
            if (await roleManager.RoleExistsAsync(role.Code))
                continue;

            var result = await roleManager.CreateAsync(AppRole.Create(role.Code, role.Name, isSystem: true));
            if (!result.Succeeded)
                logger.LogWarning("Failed to seed role {Role}: {Errors}", role.Code,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        await db.SaveChangesAsync();

        // 3. Grant ALL permissions to System and Administrator (idempotent).
        //    Runs every boot so new permissions are automatically granted.
        var allPermissions = await db.Permissions.ToListAsync();
        var privilegedRoles = new[] { Roles.System.Code, Roles.Administrator.Code };

        foreach (var roleCode in privilegedRoles)
        {
            var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == roleCode);
            if (role is null) continue;

            var grantedIds = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id)
                .Select(rp => rp.PermissionId)
                .ToListAsync();

            var grantedSet = new HashSet<Guid>(grantedIds);

            foreach (var permission in allPermissions)
            {
                if (grantedSet.Contains(permission.Id))
                    continue;

                db.RolePermissions.Add(RolePermission.Grant(role.Id, permission.Id));
                logger.LogInformation("RoleSeeder: granted {Permission} to {Role}", permission.Code, roleCode);
            }
        }

        await db.SaveChangesAsync();

        // 4. Default grants to NON-privileged roles, from the declarative table below.
        //    Additive and idempotent: it never revokes, so a tenant that tightened a role by hand
        //    keeps its decision, and a permission added to the table reaches existing tenants on
        //    the next boot.
        await SeedDefaultGrantsAsync(db, logger);
    }

    /// <summary>
    /// Permissions a role holds out of the box, beyond the everything-grant that System and
    /// Administrator receive above.
    ///
    /// <para>
    /// Until the Integration module arrived, every role other than those two was seeded with
    /// nothing and an administrator had to grant by hand through <c>role:manage-permissions</c>.
    /// That is still the rule for anything sensitive; this table exists for the permissions whose
    /// own specification names a default role, and it is deliberately short.
    /// </para>
    ///
    /// <para>
    /// <b>Role mapping.</b> INT-11 names a « superviseur » and a « contrôle interne », neither of
    /// which is a role of this platform. They are read as the closest existing ones —
    /// <see cref="Roles.SalesManager"/> and <see cref="Roles.BranchManager"/> for a supervisor,
    /// <see cref="Roles.RegulationManager"/> for internal control. The mapping is recorded in
    /// docs/integration-module-plan.md; it is a reading of the specification, not something the
    /// specification states.
    /// </para>
    /// </summary>
    private static readonly (string RoleCode, string PermissionCode)[] DefaultGrants =
    [
        // INT-11 — reading the integration state is a supervision task, acting on it is not.
        (Roles.SalesManager.Code, "Integration.Command.View"),
        (Roles.BranchManager.Code, "Integration.Command.View"),

        // The live balance is what an agent needs at the counter; it is also the one operation an
        // agent can trigger that puts load on the IMF's core banking system, which is why it is a
        // permission of its own rather than part of a read role.
        (Roles.Agent.Code, "CoreBanking.Balance.ViewLive"),
        (Roles.CommercialAgent.Code, "CoreBanking.Balance.ViewLive"),
        (Roles.SalesManager.Code, "CoreBanking.Balance.ViewLive"),
        (Roles.BranchManager.Code, "CoreBanking.Balance.ViewLive"),

        // Internal control reads the reconciliation; resolving a gap stays with the administrator.
        (Roles.RegulationManager.Code, "Integration.Reconciliation.View"),
    ];

    private static async Task SeedDefaultGrantsAsync(AdministrationDbContext db, ILogger logger)
    {
        var wanted = DefaultGrants
            .GroupBy(g => g.RoleCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToArray(), StringComparer.Ordinal);

        var permissionsByCode = await db.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.Id, StringComparer.Ordinal);

        var added = 0;

        foreach (var (roleCode, permissionCodes) in wanted)
        {
            var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == roleCode);
            if (role is null)
            {
                logger.LogWarning(
                    "RoleSeeder: role {Role} is absent, so its default grants were skipped", roleCode);
                continue;
            }

            var grantedIds = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id)
                .Select(rp => rp.PermissionId)
                .ToListAsync();

            var grantedSet = new HashSet<Guid>(grantedIds);

            foreach (var code in permissionCodes)
            {
                // A code in the table that is not in Permissions.All is a typo, and a silent one:
                // the grant would simply never happen. Logged loudly rather than ignored.
                if (!permissionsByCode.TryGetValue(code, out var permissionId))
                {
                    logger.LogError(
                        "RoleSeeder: default grant references unknown permission {Permission}; "
                        + "add it to Permissions.All or fix the code", code);
                    continue;
                }

                if (grantedSet.Contains(permissionId)) continue;

                db.RolePermissions.Add(RolePermission.Grant(role.Id, permissionId));
                grantedSet.Add(permissionId);
                added++;

                logger.LogInformation(
                    "RoleSeeder: granted default {Permission} to {Role}", code, roleCode);
            }
        }

        if (added == 0) return;

        await db.SaveChangesAsync();
    }
}
