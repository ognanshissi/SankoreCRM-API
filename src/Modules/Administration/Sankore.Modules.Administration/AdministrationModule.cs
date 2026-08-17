using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Login;
using Sankore.Modules.Administration.Features.Register;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;

namespace Sankore.Modules.Administration;

/// <summary>
/// Composition root of the Users module. The ONE public static class the
/// Bootstrapper calls — everything else inside this assembly is internal.
/// </summary>
public static class AdministrationModule
{
    public static IServiceCollection AddAdministrationModule(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AdministrationDbContext>(opt =>
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
            .AddEntityFrameworkStores<AdministrationDbContext>();

        services.AddScoped<IAdministrationModule, AdministrationModuleFacade>();
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        // MediatR handlers + FluentValidation validators for all Features/* slices
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(AdministrationModule).Assembly));
        services.AddValidatorsFromAssembly(typeof(AdministrationModule).Assembly);

        services.AddOutboxForModule<AdministrationDbContext>();

        return services;
    }

    /// <summary>
    /// Runs EF migrations and seeds system roles. Call once at startup after
    /// the DI container is built (inside a scoped block in Program.cs).
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<AdministrationDbContext>();
        await db.Database.MigrateAsync();
        await RoleSeeder.SeedAsync(sp);
        await PermissionSeeder.SeedAsync(sp);
    }

    public static IEndpointRouteBuilder MapAdministrationModuleEndpoints(this IEndpointRouteBuilder app)
    {
        return app.MapGroup("api/administration")
            .MapRegister()
            .MapLogin();
    }

}
