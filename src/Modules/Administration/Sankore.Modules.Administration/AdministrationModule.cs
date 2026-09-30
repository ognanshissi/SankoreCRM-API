using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Modules.Administration.Features.PermissionsCatalog;
using Sankore.Modules.Administration.Features.CompanyInfo;
using Sankore.Modules.Administration.Features.Authentication.AccountActivation;
using Sankore.Modules.Administration.Features.Authentication.ForgotPassword;
using Sankore.Modules.Administration.Features.Authentication.Login;
using Sankore.Modules.Administration.Features.Authentication.Logout;
using Sankore.Modules.Administration.Features.Authentication.ResetPassword;
using Sankore.Modules.Administration.Features.Authentication.RefreshToken;
using Sankore.Modules.Administration.Features.Authentication.VerifyToken;
using Sankore.Modules.Administration.Features.NotificationSettings;
using Sankore.Modules.Administration.Features.Products;
using Sankore.Modules.Administration.Features.Roles;
using Sankore.Modules.Administration.Features.Territories;
using Sankore.Modules.Administration.Features.Users;
using Sankore.Modules.Administration.Features.ImportUsers;
using Sankore.Modules.Administration.Features.ImportUsers.ValidateImport;
using Sankore.Modules.Administration.Features.Users.GetCurrentUser;
using Sankore.Modules.Administration.Features.Users.GetLoginHistory;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.FileStore;
using Sankore.Shared.Infrastructure.Google;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Administration;

/// <summary>
/// Composition root of the Administration module. The ONE public static class the
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
                    o => o.MigrationsHistoryTable("__EFMigrationsHistory", "administration"))
                .UseSnakeCaseNamingConvention());

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
            // Account activation gets its own provider so its lifespan can be days without
            // dragging password-reset links along with it — see IdentityTokenOptions.
            .AddTokenProvider<ActivationTokenProvider>(ActivationTokens.ProviderName)
            .AddEntityFrameworkStores<AdministrationDbContext>();

        AddTokenLifespans(services, config);

        services.AddScoped<IAdministrationModule, AdministrationModuleFacade>();
        // Agency perimeter (Kernel contract) — Administration owns the agency tree.
        services.AddScoped<IAgencyScopeProvider, AgencyScopeProvider>();
        services.AddScoped<Features.Agencies.CreateAgency.IAgencyCodeGenerator,
                           Features.Agencies.CreateAgency.PostgresAgencyCodeGenerator>();
        services.AddScoped<Features.Agencies.AssignAgencyManager.AgencyManagerRoleCoordinator>();
        services.AddScoped<Infrastructure.ModuleEmailSender>();
        services.AddScoped<Features.Agencies.AssignAgencyManager.AgencyManagerNotifier>();
        services.AddScoped<Features.Users.BulkAssign.BulkAssignNotifier>();
        services.AddScoped<Features.Users.AssignManager.ReportingLine>();
        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        // MediatR handlers + FluentValidation validators for all Features/* slices
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(AdministrationModule).Assembly));
        // includeInternalTypes: validators here are mostly `internal sealed`, and the
        // assembly scan skips those by default — without this they never run.
        services.AddValidatorsFromAssembly(typeof(AdministrationModule).Assembly, includeInternalTypes: true);

        services.AddOutboxForModule<AdministrationDbContext>();
        services.AddLocalization(opts => opts.ResourcesPath = "Resources");

        // User import — multi-source (CSV, Excel, Google Sheets, Google Contacts)
        services.AddLocalFileStore();
        services.AddTransient<Features.ImportUsers.ProcessUserImportJob>();
        services.AddTransient<Features.ImportUsers.Readers.FileImportReader>();
        services.AddTransient<Features.ImportUsers.Readers.GoogleSheetsImportReader>();
        services.AddTransient<Features.ImportUsers.Readers.GoogleContactsImportReader>();
        services.AddGoogleImportSettings(config);

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
    }

    /// <summary>
    /// Binds the <c>Identity</c> section onto the two token providers that issue links.
    ///
    /// Validated at start-up rather than at first use: a zero or negative lifespan — what a
    /// mistyped TimeSpan binds to — would make every activation and reset link fail the moment
    /// it was clicked, and the only visible symptom is a 400 that reads like an expired link.
    /// </summary>
    internal static void AddTokenLifespans(IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<IdentityTokenOptions>()
            .Bind(config.GetSection(IdentityTokenOptions.SectionName))
            .Validate(
                o => o.ActivationTokenLifespan > TimeSpan.Zero,
                $"{IdentityTokenOptions.SectionName}:ActivationTokenLifespan must be a positive "
                + "TimeSpan, e.g. \"7.00:00:00\" for seven days.")
            .Validate(
                o => o.PasswordResetTokenLifespan > TimeSpan.Zero,
                $"{IdentityTokenOptions.SectionName}:PasswordResetTokenLifespan must be a positive "
                + "TimeSpan, e.g. \"02:00:00\" for two hours.")
            .ValidateOnStart();

        // Identity's own providers read DataProtectionTokenProviderOptions: password reset, email
        // confirmation and change-email all share it. Activation no longer does.
        services.AddOptions<DataProtectionTokenProviderOptions>()
            .Configure<IOptions<IdentityTokenOptions>>((o, tokens) =>
                o.TokenLifespan = tokens.Value.PasswordResetTokenLifespan);

        services.AddOptions<ActivationTokenProviderOptions>()
            .Configure<IOptions<IdentityTokenOptions>>((o, tokens) =>
                o.TokenLifespan = tokens.Value.ActivationTokenLifespan);
    }

    public static IEndpointRouteBuilder MapAdministrationModuleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapLogin();
        app.MapRefreshToken();
        app.MapLogout();
        app.MapResetPassword();
        app.MapVerifyToken();
        app.MapForgotPassword();
        app.MapAccountActivation();
        app.MapGetCurrentUser();
        app.MapGetLoginHistory();
        app.MapUsersEndpoints();
        app.MapTerritoriesEndpoints();
        app.MapAgenciesEndpoints();
        app.MapRolesEndpoints();
        app.MapPermissionsEndpoints();
        app.MapProductsEndpoints();
        app.MapNotificationSettingsEndpoints();
        app.MapCompanyInfoEndpoints();
        app.MapImportUsersEndpoints();
        app.MapValidateImport();
        return app;
    }
}
