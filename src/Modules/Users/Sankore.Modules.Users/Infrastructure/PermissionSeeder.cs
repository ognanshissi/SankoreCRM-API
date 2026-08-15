using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Users.Domain;

namespace Sankore.Modules.Users.Infrastructure;

internal static class PermissionSeeder
{
    internal static async Task SeedAsync(IServiceProvider sp)
    {

        var dbContext = sp.GetRequiredService<UsersDbContext>();
        var logger = sp.GetRequiredService<ILogger<UsersDbContext>>();

        foreach (var permission in Permissions.All)
        {
            var existingPermission = await dbContext.Set<Permission>().FirstOrDefaultAsync(x => x.Code  == permission.Code);
            if (existingPermission is not null)
                continue;

            var payload = Permission.Create(permission.Code, permission.Description, permission.Module, permission.Action);
            await dbContext.Set<Permission>().AddAsync(payload, CancellationToken.None);
        }

        await dbContext.SaveChangesAsync();
    }
}