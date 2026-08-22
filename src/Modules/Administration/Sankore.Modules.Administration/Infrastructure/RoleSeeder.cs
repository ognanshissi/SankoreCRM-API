using Microsoft.AspNetCore.Identity;
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

        foreach (RoleItem role in Roles.All)
        {
            if (await roleManager.RoleExistsAsync(role.Code))
                continue;

            var result = await roleManager.CreateAsync(AppRole.Create(role.Code, role.Name, isSystem: true));
            if (!result.Succeeded)
                logger.LogWarning("Failed to seed role {Role}: {Errors}", role.Code,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
