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
using Sankore.Modules.Kyc.Features.Limits;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Features.RemoteInvitations;
using Sankore.Modules.Kyc.Features.Routing;
using Sankore.Modules.Kyc.Features.Reviews;
using Sankore.Modules.Kyc.Features.Settings;
using Sankore.Modules.Kyc.Features.Verification;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Crypto;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.PublicApi;

using Microsoft.Extensions.Options;

using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// Composition root of module M02 (KYC). Registered from the bootstrapper with
/// <c>services.AddKycModule(configuration)</c>, exactly like every other module.
/// </summary>
/// <summary>
/// Names this module's slot in the object store. A constant rather than a literal at each call
/// site: it is both the DI key and, for a bucket deployment, the bucket — "one bucket per
/// concern" stops being true the moment two spellings of the same word exist.
/// </summary>
internal static class KycObjectStorage
{
    public const string Concern = "kyc-documents";
}

public static class KycModule
{
    /// <summary>
    /// The concern this module stores its evidence under — the DI key of its
    /// <c>IObjectBackend</c> and, on a bucket deployment, the bucket name.
    ///
    /// <para>
    /// Exposed so the host can declare the backend BEFORE the module registers its filesystem
    /// fallback. The module itself must not choose the medium: doing so would mean referencing
    /// the S3 client from here, and this assembly is loaded by hosts that store no object.
    /// </para>
    /// </summary>
    public static string ObjectStorageConcern => KycObjectStorage.Concern;

    /// <summary>
    /// Name of the authentication scheme and of the policy that carry a remote-KYC SESSION
    /// (KRM-B-05).
    ///
    /// <para>
    /// Exposed so the host can register the scheme: a module cannot — the JWT bearer middleware is
    /// configured once, on the host, and this is the one value both sides must agree on. The
    /// internal constant it mirrors is what the endpoints require.
    /// </para>
    /// </summary>
    public static string RemoteSessionScheme
        => Infrastructure.RemoteLinks.KycRemoteSessionClaims.SchemeName;

    /// <summary>Audience of a session token. Accepted by nothing else in the platform.</summary>
    public static string RemoteSessionAudience
        => Infrastructure.RemoteLinks.KycRemoteSessionOptions.Audience;

    /// <summary>
    /// The session's signing identity, for the host to configure its bearer scheme with.
    ///
    /// <para>
    /// Read here and passed in rather than resolved from the container, because the host needs it
    /// at <c>AddJwtBearer</c> time — before any service provider exists. The same options instance
    /// is registered as a singleton by <see cref="AddKycModule"/>, so the issuer and the key cannot
    /// differ between the token that is minted and the token that is validated.
    /// </para>
    /// </summary>
    public static Infrastructure.RemoteLinks.KycRemoteSessionOptions ReadRemoteSessionOptions(
        IConfiguration config)
        => Infrastructure.RemoteLinks.KycRemoteSessionOptions.FromConfiguration(config);

    /// <summary>
    /// Where this module's evidence lives when no bucket is configured — <c>Kyc:Storage:BasePath</c>,
    /// else <c>kyc-documents/</c> under the content root. Only the host can see the content root,
    /// which is why it is resolved there and not inside the store.
    /// </summary>
    public static string ResolveObjectStorageBasePath(IConfiguration config, IHostEnvironment env)
        => Infrastructure.Storage.KycStorageOptions.ResolveBasePath(config, env);

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
                    opts.BasePath = Infrastructure.Storage.KycStorageOptions.ResolveBasePath(config, env);
            });

        // The medium, keyed to this concern so a second one (imports) can be registered in the
        // same container without either stealing the other's bucket or root. KycDocumentStore
        // keeps the encryption, the reference shape, the size ceiling and the digest whichever
        // backend wins.
        //
        // TryAdd, so this is only the FALLBACK. A host that configures a bucket registers an
        // R2 backend under the same key first (Sankore.Api does, via AddObjectBackend) and this
        // registration then does nothing — which is why the S3 client never has to reach this
        // module, nor the two hosts that store no object at all.
        services.TryAddKeyedSingleton<IObjectBackend>(KycObjectStorage.Concern, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<Infrastructure.Storage.KycStorageOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LocalObjectBackend>>();

            return new LocalObjectBackend(
                Infrastructure.Storage.KycStorageOptions.ResolveBasePath(options, logger), logger);
        });

        services.TryAddSingleton<Infrastructure.Storage.IKycDocumentStore>(sp =>
            new Infrastructure.Storage.KycDocumentStore(
                sp.GetRequiredKeyedService<IObjectBackend>(KycObjectStorage.Concern),
                sp.GetRequiredService<IOptions<Infrastructure.Storage.KycStorageOptions>>(),
                sp.GetRequiredService<ILogger<Infrastructure.Storage.KycDocumentStore>>()));

        // Biometry service client. The per-attempt timeout lives inside HttpBiometryClient; this
        // handler is what retries a transient 5xx and opens the circuit when the service is down —
        // the two are not substitutes. Same stack as the Leads puller.
        // BoundedResponseHandler is INSIDE the resilience handler's chain on purpose: an oversized
        // answer is refused per attempt, so a misrouted URL answering with a 5 MB login page is
        // never buffered, retried, and buffered again.
        services.AddHttpClient(Infrastructure.Biometry.HttpBiometryClient.HttpClientName)
            .AddStandardResilienceHandler();

        services.AddTransient<Infrastructure.Biometry.BoundedResponseHandler>();

        services.AddHttpClient(Infrastructure.Biometry.HttpBiometryClient.HttpClientName)
            .AddHttpMessageHandler<Infrastructure.Biometry.BoundedResponseHandler>();

        // UseFake is honoured HERE, which is what its own documentation always claimed and what
        // this registration did not do: IBiometryClient was wired to the HTTP client
        // unconditionally, so the flag was dead configuration. The consequence was worse than a
        // no-op — with no way to store a service token either, every biometry call in a dev
        // environment short-circuited to BIOMETRY_NOT_CONFIGURED and the fake, built precisely to
        // run the flow without Flask, was reachable only from tests.
        //
        // Fake in dev, HTTP everywhere else. Registered as the same scoped IBiometryClient so
        // nothing downstream can tell the difference — FakeBiometryClient answers a plausible
        // success for every endpoint unless a test forces an outcome.
        var useFake = config.GetValue<bool>(
            $"{Infrastructure.Biometry.BiometryOptions.SectionName}:UseFake");

        if (useFake)
        {
            services.AddScoped<Infrastructure.Biometry.IBiometryClient>(
                _ => new Infrastructure.Biometry.FakeBiometryClient());
        }
        else
        {
            services.AddScoped<Infrastructure.Biometry.IBiometryClient,
                               Infrastructure.Biometry.HttpBiometryClient>();
        }

        // Keeps the service's own OCR / face answers for a later /v1/score, encrypted with this
        // module's key. Scoped like the encryptor it wraps.
        services.AddScoped<Infrastructure.Biometry.BiometryPayloadProtector>();

        services.Configure<Infrastructure.Biometry.BiometryOptions>(
            config.GetSection(Infrastructure.Biometry.BiometryOptions.SectionName));

        services.AddScoped<Features.Approval.KycApprovalCircuit>();

        // CIMA LBC/FT article 13.6: the proof of address a remotely-opened file needs before its
        // holder becomes a customer.
        services.AddScoped<Features.Approval.RemoteSaleEvidence>();

        // CIMA LBC/FT: the origin of patrimony and funds, which a PEP relationship cannot be
        // authorised without.
        services.AddScoped<Features.Approval.PepAuthorizationEvidence>();
        services.AddScoped<Features.Duplicates.KycDuplicateDetector>();
        services.AddScoped<Features.Reviews.KycReviewScheduler>();
        services.AddTransient<Features.Reviews.KycReviewOrchestratorJob>();
        services.AddTransient<Features.Reviews.ProcessTenantKycReviewsJob>();
        services.AddTransient<Features.Verification.ReplayKycVerificationJob>();

        // ── Remote collection links (KRM-B-03/04) ───────────────────────────
        //
        // The token minter is a SINGLETON: it holds no state and its only dependency is the
        // platform's CSPRNG. Everything else is scoped like the rest of the module.
        services.TryAddSingleton<Infrastructure.RemoteLinks.IKycRemoteLinkTokens,
                                 Infrastructure.RemoteLinks.KycRemoteLinkTokens>();

        // "May the caller act on this file?", shared by every agent-facing single-file slice. One
        // place, because a perimeter check each slice re-implements is one a slice will be missing.
        services.AddScoped<Features.Files.KycFileAccess>();

        services.AddScoped<Features.RemoteInvitations.KycRemoteEligibility>();
        services.AddScoped<Features.RemoteInvitations.KycRemoteInvitationRevoker>();

        // ── The customer's own journey (KRM-B-05/06/07) ─────────────────────
        //
        // The session's signing identity is a SINGLETON built once from configuration: it derives a
        // key with HKDF and must not do that per request. Everything else is scoped.
        services.TryAddSingleton(sp =>
            Infrastructure.RemoteLinks.KycRemoteSessionOptions.FromConfiguration(config));

        services.TryAddScoped<Infrastructure.RemoteLinks.IKycRemoteSessions,
                              Infrastructure.RemoteLinks.KycRemoteSessions>();

        services.AddScoped<Features.PublicRemote.KycRemoteGate>();
        services.AddScoped<Features.PublicRemote.KycRemoteJourney>();
        services.AddScoped<Features.PublicRemote.KycRemoteQualityGate>();
        services.AddScoped<Features.PublicRemote.KycRemoteSubmission>();
        services.AddTransient<Features.RemoteInvitations.SendKycRemoteInvitationJob>();
        services.AddTransient<Features.RemoteInvitations.Expiry.KycRemoteInvitationExpiryOrchestratorJob>();
        services.AddTransient<Features.RemoteInvitations.Expiry.ExpireTenantKycRemoteInvitationsJob>();

        // ── Routing and the control of unattended decisions (KRM-B-08…11) ───
        services.AddScoped<Features.Routing.Controls.KycAutoApprovalSampler>();

        // KRM-B-12. Sends through M08's SMS channel, over M01's send-to-client contract — see
        // SmsKycOtpSender for why it goes through M01 rather than straight to M08. Availability is
        // per TENANT: an institution that has not configured a provider cannot enable the second
        // factor, which is what stops its customers being locked out of their own journey.
        //
        // TryAdd so a deployment that wants its own delivery — an in-house gateway, a different
        // aggregator — registers it before this line and keeps it.
        services.TryAddScoped<Infrastructure.RemoteLinks.IKycOtpSender,
                              Infrastructure.RemoteLinks.SmsKycOtpSender>();
        services.AddTransient<Features.Routing.ReviewSla.KycRemoteReviewSlaOrchestratorJob>();
        services.AddTransient<Features.Routing.ReviewSla.ChaseTenantKycRemoteReviewsJob>();

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

    /// <summary>
    /// The customer-facing surface of remote KYC, mounted by the bootstrapper under
    /// <c>api/v1/public</c> — a DIFFERENT group from the one below, with no authentication and its
    /// own rate-limiting policy.
    ///
    /// <para>
    /// Separate entry point rather than a branch inside <see cref="MapKycModuleEndpoints"/>,
    /// because the host has to be able to apply a different policy to it — and because nothing
    /// should be able to land on the anonymous surface by being added to the wrong aggregator.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapKycPublicEndpoints(this IEndpointRouteBuilder app)
        => app.MapKycPublicRemoteEndpoints();

    /// <summary>Mounted by the bootstrapper under <c>api/v1</c>.</summary>
    public static IEndpointRouteBuilder MapKycModuleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapKycFilesEndpoints();
        app.MapKycLimitsEndpoints();
        app.MapKycVerificationEndpoints();
        app.MapKycCorrectionsEndpoints();
        app.MapKycDuplicatesEndpoints();
        app.MapKycDocumentsEndpoints();
        app.MapKycApprovalEndpoints();
        app.MapKycReviewsEndpoints();
        app.MapKycRemoteInvitationsEndpoints();
        app.MapKycRoutingEndpoints();
        app.MapKycSettingsEndpoints();
        return app;
    }
}
