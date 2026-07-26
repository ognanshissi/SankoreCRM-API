using Microsoft.AspNetCore.Identity;
using Sankore.Modules.Users.Domain;

namespace Sankore.Modules.Users;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Users.Infrastructure;
using Sankore.Modules.Users.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;

/// <summary>
/// Composition root of the Users module. This is the ONE public static
/// class the Bootstrapper calls to wire the whole module — everything else
/// inside the module's main assembly is internal.
/// </summary>
public static class UsersModule
{
    public static IServiceCollection AddUsersModule(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<UsersDbContext>(opt =>
            opt.UseNpgsql(
                config.GetConnectionString("Database"),
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity")));

        services.AddIdentity<AppUser, IdentityRole<Guid>>()
            .AddEntityFrameworkStores<UsersDbContext>();
        
        services.AddScoped<IUsersModule, UsersModuleFacade>();
        

        services.AddOutboxForModule<UsersDbContext>();

        return services;
    }
}
