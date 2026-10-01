namespace Sankore.Modules.Kyc;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Features.Corrections;
using Sankore.Modules.Kyc.Features.Documents;
using Sankore.Modules.Kyc.Features.Duplicates;
using Sankore.Modules.Kyc.Features.Files;
using Sankore.Modules.Kyc.Features.Reviews;
using Sankore.Modules.Kyc.Features.Verification;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Crypto;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.PublicApi;

using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Composition root of module M02 (KYC). Registered from the bootstrapper with
/// <c>services.AddKycModule(configuration)</c>, exactly like every other module.
/// </summary>
public static class KycModule
{
    public static IServiceCollection AddKycModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<KycDbContext>(opt =>
            opt.UseNpgsql(
                    config.GetConnectionString("Database"),
                    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "kyc"))
                .UseSnakeCaseNamingConvention());

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(KycModule).Assembly));

        // includeInternalTypes: this module's validators are `internal sealed`, and the assembly
        // scan skips internals by default — without this they never run, and a command reaches
        // its handler unvalidated.
        services.AddValidatorsFromAssembly(typeof(KycModule).Assembly, includeInternalTypes: true);

        services.AddOutboxForModule<KycDbContext>();

        // Field protection, keyed to this module — see KycFieldProtection for why the shared
        // AddFieldProtection cannot be called a second time.
        services.AddKycFieldProtection(config);

        // NOT calling AddFieldProtection(config, "Kyc") — deliberately.
        //
        // FieldProtectionOptions is one options instance for the whole container and
        // IFieldEncryptor one singleton, so a second section does not give this module its own
        // key: it overwrites M01's. Measured, not assumed — registering "Customers" then "Kyc"
        // resolves to the Kyc key for both, which would make every client field M01 has already
        // encrypted undecryptable. AddFieldProtection now throws on that second call.
        //
        // M02 encrypts nothing yet; identity document numbers arrive in the verification phase.
        // Before they do, this module needs its own options type and its own encryptor instance
        // (named options plus a keyed service), not a second call to the shared helper.

        services.TryAddSingleton(TimeProvider.System);

        // Document storage: encrypted objects for selfies and identity images. Its key is a
        // SEPARATE setting from Kyc:FieldEncryptionKey — one protects columns, the other protects
        // files, and a single compromised key must not open both.
        services.Configure<Infrastructure.Storage.KycStorageOptions>(
            config.GetSection(Infrastructure.Storage.KycStorageOptions.SectionName));

        // BasePath is resolved here rather than inside the store: only the host can see the
        // content root, and an IOptions binding cannot.
        services.AddOptions<Infrastructure.Storage.KycStorageOptions>()
            .PostConfigure<IHostEnvironment>((opts, env) =>
            {
                if (string.IsNullOrWhiteSpace(opts.BasePath))
                    opts.BasePath = Infrastructure.Storage.LocalKycDocumentStore.ResolveBasePath(config, env);
            });

        services.AddSingleton<Infrastructure.Storage.IKycDocumentStore,
                              Infrastructure.Storage.LocalKycDocumentStore>();

        // Biometry service client. The per-attempt timeout lives inside HttpBiometryClient; this
        // handler is what retries a transient 5xx and opens the circuit when the service is down —
        // the two are not substitutes. Same stack as the Leads puller.
        services.AddHttpClient(Infrastructure.Biometry.HttpBiometryClient.HttpClientName)
            .AddStandardResilienceHandler();

        services.AddScoped<Infrastructure.Biometry.IBiometryClient,
                           Infrastructure.Biometry.HttpBiometryClient>();

        services.Configure<Infrastructure.Biometry.BiometryOptions>(
            config.GetSection(Infrastructure.Biometry.BiometryOptions.SectionName));

        services.AddScoped<Features.Approval.KycApprovalCircuit>();
        services.AddScoped<Features.Duplicates.KycDuplicateDetector>();
        services.AddScoped<Features.Reviews.KycReviewScheduler>();
        services.AddTransient<Features.Reviews.KycReviewOrchestratorJob>();
        services.AddTransient<Features.Reviews.ProcessTenantKycReviewsJob>();
        services.AddTransient<Features.Verification.ReplayKycVerificationJob>();

        services.AddScoped<IKycSettings, KycSettingsService>();
        services.AddScoped<IKycModule, KycModuleFacade>();

        return services;
    }

    /// <summary>
    /// Applies this module's migrations, then seeds the per-tenant parameters. Called from the
    /// bootstrapper's start-up scope alongside the other modules.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<KycDbContext>();
        await db.Database.MigrateAsync();

        await KycSettingsSeeder.SeedAsync(
            db,
            sp.GetRequiredService<ITenantStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<KycDbContext>>());
    }

    /// <summary>Mounted by the bootstrapper under <c>api/v1</c>.</summary>
    public static IEndpointRouteBuilder MapKycModuleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapKycFilesEndpoints();
        app.MapKycVerificationEndpoints();
        app.MapKycCorrectionsEndpoints();
        app.MapKycDuplicatesEndpoints();
        app.MapKycDocumentsEndpoints();
        app.MapKycApprovalEndpoints();
        app.MapKycReviewsEndpoints();
        return app;
    }
}
