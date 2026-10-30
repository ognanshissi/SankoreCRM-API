namespace Sankore.Modules.Customers;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients;
using Sankore.Modules.Customers.Features.Compliance;
using Sankore.Modules.Customers.Features.ContactPoints;
using Sankore.Modules.Customers.Features.Duplicates;
using Sankore.Modules.Customers.Features.Groups;
using Sankore.Modules.Customers.Features.LeadConversion;
using Sankore.Modules.Customers.Features.LegalEntities;
using Sankore.Modules.Customers.Features.Lifecycle;
using Sankore.Modules.Customers.Features.Documents;
using Sankore.Modules.Customers.Features.Complaints;
using Sankore.Modules.Customers.Features.Holdings;
using Sankore.Modules.Customers.Features.Household;
using Sankore.Modules.Customers.Features.Relationships;
using Sankore.Modules.Customers.Features.Timeline;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.FileStore;
using Sankore.Shared.Kernel;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Customers.Features.Import;

/// <summary>
/// Composition root of module M01 (Customers): the only file the host touches to wire
/// the whole module, and the only place the module's internals are visible from outside
/// its own folders.
/// </summary>
public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services, IConfiguration config)
    {
        // The same per-tenant start as InitializeAsync, for a tenant created while the host runs.
        services.AddSingleton<Sankore.Shared.Infrastructure.Tenants.ITenantSeeder>(new Sankore.Shared.Infrastructure.Tenants.TenantSeeder("Customers", async (sp, ct) =>
        {
            await CustomerSeeder.SeedAsync(
                sp.GetRequiredService<CustomersDbContext>(),
                sp.GetRequiredService<ITenantStore>(),
                sp.GetRequiredService<ILogger<CustomersDbContext>>(),
                ct);
        }));

        services.AddDbContext<CustomersDbContext>(opt =>
            opt.UseNpgsql(
                    config.GetConnectionString("Database"),
                    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "customers"))
                .UseSnakeCaseNamingConvention());

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(CustomersModule).Assembly));

        // includeInternalTypes: this module's validators are `internal sealed`, and the
        // assembly scan skips internals by default — without this they never run.
        services.AddValidatorsFromAssembly(typeof(CustomersModule).Assembly, includeInternalTypes: true);

        // Assigned-only reads: an Agent or a CommercialAgent sees the clients they advise.
        services.AddScoped<Features.Clients.Shared.ClientVisibility>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(Features.Clients.Shared.ClientVisibilityBehavior<,>));

        services.AddOutboxForModule<CustomersDbContext>();

        // Nightly technical purge (TechnicalPurgeJob).
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITechnicalPurge, Infrastructure.Retention.CustomersInboxPurge>();
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITechnicalPurge, Infrastructure.Retention.ExpiredExportFilesPurge>();
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITechnicalPurge, Infrastructure.Retention.ClientImportFilesPurge>();
        // Emptying a TEST institution (data-purge lot 4).
        services.AddScoped<Sankore.Shared.Infrastructure.Retention.ITenantDataWipe, Infrastructure.Retention.CustomersTenantDataWipe>();
        services.AddLocalization(o => o.ResourcesPath = "Resources");

        // Field-level AES-256-GCM encryption + HMAC blind indexes for this module's
        // keys (Customers:FieldEncryptionKey / Customers:BlindIndexKey). Registering
        // per module keeps one module's compromised key from exposing another's data.
        services.AddFieldProtection(config, "Customers");

        // Backing store for client exports (US-M01-BE-29).
        services.AddLocalFileStore();

        // Documents filed on the client record (2026-10-26): their own encrypted store and key,
        // Customers:Documents:EncryptionKey. No key = no store registered, and an upload answers
        // CLIENT_DOCUMENTS_UNAVAILABLE rather than writing a file nobody could ever read.
        services.Configure<Infrastructure.Documents.ClientDocumentStorageOptions>(
            config.GetSection(Infrastructure.Documents.ClientDocumentStorageOptions.SectionName));

        services.TryAddKeyedSingleton<Sankore.Shared.ObjectStorage.IObjectBackend>(
            Infrastructure.Documents.ClientDocumentStorage.Concern, (sp, _) =>
            {
                var options = sp.GetRequiredService<IOptions<Infrastructure.Documents.ClientDocumentStorageOptions>>().Value;
                var root = string.IsNullOrWhiteSpace(options.BasePath)
                    ? Path.Combine(AppContext.BaseDirectory, Infrastructure.Documents.ClientDocumentStorage.Concern)
                    : Path.GetFullPath(options.BasePath);
                return new Sankore.Shared.ObjectStorage.LocalObjectBackend(
                    root, sp.GetRequiredService<ILogger<Sankore.Shared.ObjectStorage.LocalObjectBackend>>());
            });

        if (!string.IsNullOrWhiteSpace(config[$"{Infrastructure.Documents.ClientDocumentStorageOptions.SectionName}:EncryptionKey"]))
        {
            services.TryAddSingleton<Infrastructure.Documents.IClientDocumentStore>(sp =>
                new Infrastructure.Documents.EncryptedClientDocumentStore(
                    sp.GetRequiredKeyedService<Sankore.Shared.ObjectStorage.IObjectBackend>(Infrastructure.Documents.ClientDocumentStorage.Concern),
                    sp.GetRequiredService<IOptions<Infrastructure.Documents.ClientDocumentStorageOptions>>(),
                    sp.GetRequiredService<ILogger<Infrastructure.Documents.EncryptedClientDocumentStore>>()));
        }

        // Nothing registers TimeProvider by default — not ASP.NET Core, not this repo — yet
        // 13 handlers and jobs in this module take it so their clock can be frozen in tests.
        // Without this line every one of them fails to resolve at run time. TryAdd so a host
        // (or a test) can substitute a fake clock.
        services.TryAddSingleton(TimeProvider.System);

        // ── Module-internal services ────────────────────────────────────────
        services.AddScoped<ICustomerSettings, CustomerSettingsService>();
        services.AddScoped<IClientNumberGenerator, ClientNumberGenerator>();
        services.AddScoped<IInboxGuard, InboxGuard>();
        services.AddScoped<IPhoneticKeyCalculator, WestAfricanPhoneticKeyCalculator>();

        // The single path from a client id to a sent email (KRM-B-03). Shared by the welcome
        // consumer and by ICustomersModule.NotifyClientAsync, so the address is decrypted in one
        // place and the locale fallback is written once.
        services.AddScoped<Infrastructure.Notifications.ClientEmailNotifier>();

        // The client-number + date-of-birth check behind the public remote-KYC page (KRM-B-05).
        // It lives here because both values are M01's and neither may cross the module boundary.
        services.AddScoped<Infrastructure.RemoteIdentity.RemoteIdentityVerifier>();
        services.AddScoped<Infrastructure.RemoteIdentity.ReadIdentityComparer>();
        services.AddScoped<Infrastructure.Notifications.ClientSmsNotifier>();
        // Writes to the client when a workflow « Notification » step on a client record asks.
        services.AddScoped<Features.WorkflowNotifications.ClientWorkflowNotifier>();
        // What a workflow « Condition » step on a client record may read.
        services.AddScoped<Sankore.Shared.Infrastructure.Workflow.IContextProvider, Infrastructure.ClientContextProvider>();
        services.AddScoped<Sankore.Shared.Infrastructure.Workflow.IWorkflowRecordGate, Infrastructure.ClientRecordGate>();

        // The single path into the complaints register, shared by the agent's endpoint and the
        // WhatsApp bot's contract (2026-10-26).
        services.AddScoped<Features.Complaints.ClientComplaintFiling>();

        // ── Cross-module surface ────────────────────────────────────────────
        services.AddScoped<ICustomersModule, CustomersModuleFacade>();

        // ── Per-area registrations (one call per vertical-slice folder) ──────
        services.AddClientsServices();
        services.AddContactPointsServices();
        services.AddLifecycleServices();
        services.AddLegalEntitiesServices();
        services.AddGroupsServices();
        services.AddRelationshipsServices()
            .AddHouseholdServices()
            .AddHoldingsServices();
        services.AddDuplicatesServices();
        services.AddTimelineServices();
        services.AddComplianceServices();
        services.AddLeadConversionServices();
        services.AddImportServices();

        return services;
    }

    /// <summary>
    /// Applies migrations, then brings every active tenant up to the module's baseline
    /// configuration. Called from the startup scope in Program.cs.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);

        var db = sp.GetRequiredService<CustomersDbContext>();
        await db.Database.MigrateAsync();

        await CustomerSeeder.SeedAsync(
            db,
            sp.GetRequiredService<ITenantStore>(),
            sp.GetRequiredService<ILogger<CustomersDbContext>>(),
            CancellationToken.None);
    }

    /// <summary>
    /// Maps every area aggregator under the host's api/v1 group. Each aggregator owns
    /// its own MapGroup + tag, so adding a slice never touches this method.
    /// </summary>
    public static IEndpointRouteBuilder MapCustomersModuleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapClientsEndpoints();
        app.MapContactPointsEndpoints();
        app.MapLifecycleEndpoints();
        app.MapLegalEntitiesEndpoints();
        app.MapGroupsEndpoints();
        app.MapRelationshipsEndpoints();

        // The client's household, and the products they hold with who is designated on them
        // (BNF-B-13/14/15)
        app.MapHouseholdEndpoints();
        app.MapHoldingsEndpoints();
        // Everything the client gave or received, from every module (2026-10-26)
        app.MapDocumentsEndpoints();
        app.MapClientComplaintsEndpoints();
        app.MapDuplicatesEndpoints();
        app.MapTimelineEndpoints();
        app.MapComplianceEndpoints();
        app.MapImportClientsEndpoints();
        return app;
    }
}
