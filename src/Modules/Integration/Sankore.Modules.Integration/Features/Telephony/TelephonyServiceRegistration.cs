namespace Sankore.Modules.Integration.Features.Telephony;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Shared.ObjectStorage;

/// <summary>
/// What the telephony area needs in the container. One call, wired from
/// <c>IntegrationModule.AddIntegrationModule</c>.
///
/// <para>
/// The two jobs are <c>AddTransient</c> and not scoped, which is what Hangfire's activator
/// expects: it resolves the job type per execution from the root provider, and a scoped
/// registration there resolves from the root scope and lives for the life of the process.
/// </para>
/// </summary>
internal static class TelephonyServiceRegistration
{
    internal static IServiceCollection AddTelephonyServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Scoped, both of them: they take IntegrationDbContext and the keyed encryptors, and a
        // singleton holding a DbContext is the one lifetime mistake this repo cannot survive.
        services.AddScoped<TelephonyAccess>();
        services.AddScoped<TelephonyNumberProtector>();
        services.AddScoped<TelephonyPayloadProtector>();
        services.AddScoped<Journal.TelephonyCallMatcher>();
        services.AddScoped<Journal.TelCallJournalWriter>();
        services.AddScoped<ClickToCall.DialableNumberResolver>();
        services.AddScoped<TelephonyRecordAccess>();
        services.AddScoped<TelephonyAgencyResolver>();
        services.AddScoped<TelephonyCallPerimeter>();

        // Scoped although it holds no DbContext: IRealtimeNotifier is a singleton and this is a
        // two-field wrapper, so a singleton would work — scoped keeps every service of this area
        // on one lifetime, which is the thing a reader checks rather than re-derives.
        services.AddScoped<TelephonyScreenPop>();

        services.AddTransient<UserLinks.CheckUserLinks.CheckTelephonyUserLinksOrchestrator>();
        services.AddTransient<UserLinks.CheckUserLinks.CheckTelephonyUserLinksJob>();
        services.AddTransient<Webhook.ProcessTelephonyWebhookJob>();
        services.AddTransient<Journal.SyncTelephonyCallsJob>();

        // ── Recordings (TEL-B-15 → TEL-B-17) ────────────────────────────────
        services.AddScoped<Recordings.TelephonyRecordingPolicyReader>();
        services.AddTransient<Recordings.FetchTelephonyRecordingsJob>();
        services.AddTransient<Recordings.PurgeTelephonyRecordingsJob>();
        services.AddTransient<Recordings.FetchTelephonyRecordingsOrchestrator>();
        services.AddTransient<Recordings.PurgeTelephonyRecordingsOrchestrator>();

        // ── Indicators (TEL-B-19) ───────────────────────────────────────────
        services.AddTransient<Stats.RecomputeTelephonyStatsJob>();
        services.AddTransient<Stats.RecomputeTelephonyStatsHourlyOrchestrator>();
        services.AddTransient<Stats.RecomputeTelephonyStatsNightlyOrchestrator>();

        services.Configure<RecordingStorageOptions>(
            configuration.GetSection(RecordingStorageOptions.SectionName));

        // The medium, keyed to this concern so it cannot steal — or be stolen by — the batch
        // files', the claim pieces' or the KYC images' backend. TryAdd, so this is only the
        // FALLBACK: a host that configures a bucket registers an R2 backend under the same key
        // first and this does nothing.
        services.TryAddKeyedSingleton<IObjectBackend>(RecordingStorage.Concern, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<RecordingStorageOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LocalObjectBackend>>();

            var root = string.IsNullOrWhiteSpace(options.BasePath)
                ? Path.Combine(AppContext.BaseDirectory, RecordingStorage.Concern)
                : Path.GetFullPath(options.BasePath);

            if (string.IsNullOrWhiteSpace(options.BasePath))
            {
                logger.LogWarning(
                    "{Setting}:BasePath is not configured; call recordings will be written to "
                    + "{Fallback}, which a redeploy destroys — and unlike a batch file a recording "
                    + "cannot be regenerated, because the provider may have deleted its own copy "
                    + "at our request.", RecordingStorageOptions.SectionName, root);
            }

            return new LocalObjectBackend(root, logger);
        });

        // Registered ONLY when a key is configured, which is what makes "built but shipped
        // disabled" true of the deployment and not just of the policy: with no key there is no
        // store, the jobs resolve null and skip loudly, and the player answers "not available".
        // The alternative — registering it always — would make the store's eager key read a
        // start-up failure for every deployment that does not record calls.
        if (!string.IsNullOrWhiteSpace(
                configuration[$"{RecordingStorageOptions.SectionName}:EncryptionKey"]))
        {
            services.TryAddSingleton<IRecordingStore>(sp => new EncryptedRecordingStore(
                sp.GetRequiredKeyedService<IObjectBackend>(RecordingStorage.Concern),
                sp.GetRequiredService<IOptions<RecordingStorageOptions>>(),
                sp.GetRequiredService<ILogger<EncryptedRecordingStore>>()));
        }

        return services;
    }
}
