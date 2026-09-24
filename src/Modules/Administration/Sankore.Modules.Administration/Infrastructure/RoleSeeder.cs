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
    }
}
