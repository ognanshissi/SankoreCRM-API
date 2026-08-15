using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Users.Domain;
using Sankore.Modules.Users.Features.Login;
using Sankore.Modules.Users.Features.Register;
using Sankore.Modules.Users.Infrastructure;
using Sankore.Modules.Users.Infrastructure.JwtToken;
using Sankore.Modules.Users.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Users;

/// <summary>
/// Composition root of the Users module. The ONE public static class the
/// Bootstrapper calls — everything else inside this assembly is internal.
/// </summary>
public static class UsersModule
{
    public static IServiceCollection AddUsersModule(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<UsersDbContext>(opt =>
            opt.UseNpgsql(
                config.GetConnectionString("Database"),
                o =>
                {
                    o.MigrationsHistoryTable("__EFMigrationsHistory", "identity");
                }));

        // AddIdentityCore does NOT register cookie auth schemes, so the JWT bearer
        // default set in Program.cs remains the single authentication scheme.
        services.AddIdentityCore<AppUser>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequiredLength = 8;
                options.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddRoles<AppRole>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddEntityFrameworkStores<UsersDbContext>();

        services.AddScoped<IUsersModule, UsersModuleFacade>();
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        // MediatR handlers + FluentValidation validators for all Features/* slices
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(UsersModule).Assembly));
        services.AddValidatorsFromAssembly(typeof(UsersModule).Assembly);

        services.AddOutboxForModule<UsersDbContext>();

        return services;
    }

    /// <summary>
    /// Runs EF migrations and seeds system roles. Call once at startup after
    /// the DI container is built (inside a scoped block in Program.cs).
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<UsersDbContext>();
        await db.Database.MigrateAsync();
        await RoleSeeder.SeedAsync(sp);
        await PermissionSeeder.SeedAsync(sp);
    }

    public static IEndpointRouteBuilder MapUsersModuleEndpoints(this IEndpointRouteBuilder app)
    {
        return app.MapGroup("api/identity")
            .MapRegister()
            .MapLogin();
    }

    /// <summary>Kept for backwards compat with existing Program.cs call site.</summary>
    public static IEndpointRouteBuilder MapIdentityModuleEndpoints(this IEndpointRouteBuilder app)
        => app.MapUsersModuleEndpoints();
}
