using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Infrastructure;

internal static class PermissionSeeder
{
    internal static async Task SeedAsync(IServiceProvider sp)
    {

        var dbContext = sp.GetRequiredService<AdministrationDbContext>();
        var logger = sp.GetRequiredService<ILogger<AdministrationDbContext>>();

        foreach (var permission in Permissions.All)
        {
            var existingPermission = await dbContext.Permissions.FirstOrDefaultAsync(x => x.Code  == permission.Code);
            if (existingPermission is not null)
                continue;

            var payload = Permission.Create(permission.Code, permission.Description, permission.Module, permission.Action);
            await dbContext.Permissions.AddAsync(payload, CancellationToken.None);
        }

        await dbContext.SaveChangesAsync();
    }
}