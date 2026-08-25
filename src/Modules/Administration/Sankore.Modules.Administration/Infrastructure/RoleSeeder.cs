using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Infrastructure;

/// <summary>
/// Seeds the fixed set of system roles into the identity database on startup.
/// Idempotent — safe to run on every boot.
/// </summary>
internal static class RoleSeeder
{
    internal static async Task SeedAsync(IServiceProvider sp)
    {
        var roleManager = sp.GetRequiredService<RoleManager<AppRole>>();
        var logger = sp.GetRequiredService<ILogger<AdministrationDbContext>>();
        var db = sp.GetRequiredService<AdministrationDbContext>();
        
        // Generate permissions
        await PermissionSeeder.SeedAsync(sp);
        
        foreach (RoleItem role in Roles.All)
        {
            var roleExist = await roleManager.RoleExistsAsync(role.Code);
            if (roleExist && role.Code != Roles.System.Code)
                continue;

            if (!roleExist)
            {
                var result = await roleManager.CreateAsync(AppRole.Create(role.Code, role.Name, isSystem: true));
                if (!result.Succeeded)
                    logger.LogWarning("Failed to seed role {Role}: {Errors}", role.Code,
                        string.Join("; ", result.Errors.Select(e => e.Description)));
            }
            
            if (role.Code == Roles.System.Code || role.Code == Roles.Administrator.Code)
            {
                // permissions
                var permissions = await db.Permissions.AsQueryable().ToListAsync();
                var createdRole = await db.Roles.FirstOrDefaultAsync(x => x.Name == role.Code);

                if (permissions.Count != 0 && createdRole is not null)
                {
                    foreach (var permission in permissions)
                    {
                        var rolePermission = RolePermission.Grant(createdRole.Id, permission.Id);
                        if (!await db.RolePermissions.AnyAsync(x =>
                                x.RoleId == createdRole.Id && x.PermissionId == permission.Id))
                        {
                            await db.RolePermissions.AddAsync(rolePermission);
                        }
                    }
                }
            }
            
            await db.SaveChangesAsync();
        }
    }
}
