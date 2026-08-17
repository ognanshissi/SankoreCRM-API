using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;

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

        foreach (var name in Roles.All)
        {
            if (await roleManager.RoleExistsAsync(name))
                continue;

            var result = await roleManager.CreateAsync(AppRole.Create(name, isSystem: true));
            if (!result.Succeeded)
                logger.LogWarning("Failed to seed role {Role}: {Errors}", name,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
