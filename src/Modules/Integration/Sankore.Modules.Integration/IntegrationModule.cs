namespace Sankore.Modules.Integration;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Features.Balance;
using Sankore.Modules.Integration.Features.Batch.Inbound;
using Sankore.Modules.Integration.Features.Batch.Outbound;
using Sankore.Modules.Integration.Features.CallLog;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.Connections;
using Sankore.Modules.Integration.Features.Dispatch;
using Sankore.Modules.Integration.Features.Insurance;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.Features.Onboarding;
using Sankore.Modules.Integration.Features.Reconciliation;
using Sankore.Modules.Integration.Features.RelayAgents;
using Sankore.Modules.Integration.Features.KycLimits;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Features.References;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Extensions;

/// <summary>
/// The integration module: one socle, two families of connector (core banking and insurance).
///
/// <para>
/// Registered by the bootstrapper, like every other module. The adapters are NOT registered
/// here: they live in their own assemblies and the host chooses which to ship, which is what
/// lets a deployment carry the Temenos adapter without carrying ORASS.
/// </para>
/// </summary>
public static class IntegrationModule
{
    /// <summary>Configuration section of the module's own switches.</summary>
    public const string SectionName = "Integration";

    public static IServiceCollection AddIntegrationModule(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddDbContext<IntegrationDbContext>(opt =>
            opt.UseNpgsql(
                    config.GetConnectionString("Database"),
                    npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "integration"))
                .UseSnakeCaseNamingConvention());

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(IntegrationModule).Assembly));

        // includeInternalTypes: this module's validators are `internal sealed`, and the assembly
        // scan skips internals by default — without this they never run, and a command reaches
        // its handler unvalidated.
        services.AddValidatorsFromAssembly(typeof(IntegrationModule).Assembly, includeInternalTypes: true);

        services.AddOutboxForModule<IntegrationDbContext>();

        // This module's own encryption keys, keyed. See IntegrationFieldProtection for why a
        // second AddFieldProtection call cannot be used.
        services.AddIntegrationFieldProtection(config);

        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IntegrationAdapterResolver>();
        services.AddScoped<IIntegrationInboxGuard, IntegrationInboxGuard>();

        // One registration per Features/<Area>/ folder, the shape M01 settled on: a slice adds
        // its own services next to itself and this method gains one line, so two areas being
        // written at once cannot collide here.
        services.AddConnectionsServices();
        services.AddMappingsServices();
        // BEFORE AddCommandsServices, deliberately: the outbound slice supplies the real
        // IBatchFileEnlister and the commands slice registers a TryAdd placeholder for it.
        // Either order resolves to the real one, but the other way round leaves a no-op
        // TryAdd sitting in the file that reads as if it were the active default.
        services.AddOutboundBatchServices();
        services.AddCommandsServices();
        services.AddDispatchServices(config);
        services.AddCallLogServices();
        services.AddOnboardingServices();
        services.AddBalanceServices();
        services.AddSnapshotServices();
        services.AddSyncServices();
        services.AddKycLimitsServices();
        services.AddReconciliationServices();
        services.AddInboundBatchServices();
        services.AddRelayAgentsServices();

        // The insurance family (ASS-03 →). On the SAME socle: no second DbContext, no second
        // outbox, no second dispatcher — which is the whole claim of ASS-01.
        services.AddInsuranceServices();

        return services;
    }

    /// <summary>
    /// Runs the module's migration. Called from the startup scope in <c>Program.cs</c>.
    ///
    /// <para>
    /// No seeder: a connection is something an administrator configures, and a mapping table
    /// depends on the CBS at the other end. There is nothing this module could seed that would
    /// be true for every tenant.
    /// </para>
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);

        var db = sp.GetRequiredService<IntegrationDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>Mounted by the bootstrapper under <c>api/v1</c>.</summary>
    public static IEndpointRouteBuilder MapIntegrationModuleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapIntegrationConnectionsEndpoints();
        app.MapMappingsEndpoints();
        app.MapIntegrationCommandsEndpoints();
        app.MapIntegrationReferencesEndpoints();
        app.MapCallLogEndpoints();
        app.MapBalanceEndpoints();
        app.MapRelayAgentsEndpoints();
        app.MapReconciliationEndpoints();
        app.MapInsuranceEndpoints();

        return app;
    }

    /// <summary>
    /// The module's PUBLIC endpoints, mounted on the root app and <b>not</b> under
    /// <c>api/v1</c>: an external back office posting a notification carries no JWT, so these
    /// must be mapped before <c>UseAuthentication()</c>. The same place M13 mounts its ingest.
    ///
    /// <para>
    /// A separate method, and public while the area aggregators stay internal, because the
    /// bootstrapper has to call this one from another assembly. Keeping it on
    /// <see cref="IntegrationModule"/> rather than making the feature class public leaves the
    /// module with one public surface instead of two.
    /// </para>
    ///
    /// <para>
    /// Authentication for these routes is the HMAC signature over the request body, verified
    /// against the per-connection secret in the vault. There is no anonymous path: a request
    /// whose signature does not verify is refused, and so is one for an unknown or deactivated
    /// connection — with the same 401, so the route cannot be used to enumerate connections.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapIntegrationPublicEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapIntegrationWebhookEndpoints();

        // The relay agent's own two routes (INT-27): the enrolment exchange and the heartbeat.
        // Public because the caller is a service on the IMF's hardware with no user and no JWT —
        // its credential is the client certificate of the TLS handshake, from which the thumbprint
        // is computed server-side. Until client certificates are enabled on the route both answer
        // the same refusal, which is the correct failure mode rather than a gap.
        app.MapRelayAgentPublicEndpoints();

        return app;
    }
}

/// <summary>
/// Idempotency guard for this module's consumers.
///
/// <para>
/// Module-owned rather than shared, like M01's and M02's. The id is derived per consumer and not
/// taken raw from the message: <c>KycValidatedEvent</c> is consumed by M01 AND by this module
/// (INT-14), and guarding on the bare message id would let whichever ran first starve the other.
/// </para>
/// </summary>
public interface IIntegrationInboxGuard
{
    /// <summary>
    /// Claims a message for one consumer. <c>false</c> means it was already handled and the
    /// consumer must return immediately.
    /// </summary>
    Task<bool> TryBeginAsync(
        Guid messageId, Guid tenantId, string eventType, string consumerKey, CancellationToken ct);
}

internal sealed class IntegrationInboxGuard(
    IntegrationDbContext db,
    TimeProvider clock,
    ILogger<IntegrationInboxGuard> logger) : IIntegrationInboxGuard
{
    public async Task<bool> TryBeginAsync(
        Guid messageId, Guid tenantId, string eventType, string consumerKey, CancellationToken ct)
    {
        var id = DeriveId(messageId, consumerKey);

        // Read first, then insert — the shape M01's InboxGuard settled on, and not a belt-and-
        // braces flourish. Relying on the catch alone looked equivalent and was not: the EF
        // InMemory provider raises a BARE ArgumentException out of SaveChangesAsync rather than
        // wrapping it in a DbUpdateException, so `catch (DbUpdateException)` never fired and a
        // replayed event crashed the consumer under every test that uses it. The read covers the
        // ordinary redelivery; the catch below still covers the genuine concurrent race, which is
        // the only case a read cannot see.
        var alreadyHandled = await db.InboxMessages
            .IgnoreQueryFilters()
            .AnyAsync(m => m.Id == id, ct);

        if (alreadyHandled)
        {
            logger.LogDebug(
                "Inbox: {EventType} for {ConsumerKey} was already handled (message {MessageId})",
                eventType, consumerKey, messageId);

            return false;
        }

        db.InboxMessages.Add(new IntegrationInboxMessage
        {
            Id = id,
            TenantId = tenantId,
            EventType = $"{eventType}:{consumerKey}",
            ReceivedAt = clock.GetUtcNow(),
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A redelivery. Detach so the failed insert does not poison the rest of the unit of
            // work, and report that there is nothing to do.
            db.ChangeTracker.Clear();

            logger.LogDebug(
                "Inbox: {EventType} for {ConsumerKey} was already handled (message {MessageId})",
                eventType, consumerKey, messageId);

            return false;
        }
    }

    /// <summary>
    /// A deterministic per-consumer derivative of the message id: still exactly one row per
    /// (message, consumer), still idempotent on replay, and two consumers of the same event
    /// cannot collide on the primary key.
    /// </summary>
    private static Guid DeriveId(Guid messageId, string consumerKey)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{messageId:N}:{consumerKey}"));

        return new Guid(bytes.AsSpan(0, 16));
    }

    /// <summary>
    /// PostgreSQL only, deliberately narrow. An earlier version also matched an inner
    /// <see cref="ArgumentException"/> "because the InMemory provider reports it differently" —
    /// which was both wrong about the shape and far too broad: it would have swallowed a genuine
    /// argument fault as if it were a redelivery. The test provider is handled by the read above.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation
        };
}
