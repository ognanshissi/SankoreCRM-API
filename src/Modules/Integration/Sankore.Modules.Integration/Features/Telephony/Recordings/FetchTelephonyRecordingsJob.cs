namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Fetches the recordings this tenant's policy says to keep, and takes the provider's copy when it
/// is asked to (TEL-B-16).
///
/// <para>
/// <b>A job and not a step of the webhook, because the bytes are the expensive part.</b> A webhook
/// has to answer in milliseconds; a recording is tens of megabytes over somebody else's API, and
/// it is not ready the instant a call ends. So the journal marks a call <c>Pending</c> when the
/// provider says it HAS one, and this sweep is what turns that into a file — or into
/// <c>Failed</c>, which an operator can see.
/// </para>
///
/// <para>
/// <b>It does nothing at all for a tenant whose policy is <c>Disabled</c>, which is every tenant
/// until a compliance officer states a basis.</b> That is the whole shape of "built but shipped
/// disabled": the code is here and exercised, and a deployment that never switches it on fetches
/// nothing, stores nothing and needs no encryption key.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class FetchTelephonyRecordingsJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// The sync queue, with the other provider-facing work: it reads the same per-minute ceiling
    /// as the webhook processing and the catch-up sweep, and an operator draining one has to be
    /// able to drain them together. A literal because a <c>[Queue]</c> attribute takes a
    /// compile-time constant.
    /// </summary>
    private const string QueueName = "integration-sync";

    /// <summary>
    /// How many recordings one run fetches. A ceiling and not a target: it bounds what a run can
    /// cost the provider's rate limit and this process's memory — each file is materialised whole,
    /// which <see cref="IRecordingStore"/> says out loud — and the next run takes the rest.
    /// </summary>
    internal const int MaxPerRun = 25;

    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created: ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them. SYSTEM — a schedule elapsed.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<FetchTelephonyRecordingsJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<TelephonyAccess>(),
            sp.GetRequiredService<TelephonyRecordingPolicyReader>(),
            // GetService: the store's constructor reads the encryption key eagerly, and a tenant
            // whose policy is Disabled must not need one. A deployment that switched recording on
            // without configuring a key gets a loud failure on the first fetch, which is the right
            // moment — not at boot, for a feature nobody uses.
            sp.GetService<IRecordingStore>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "Telephony recordings for tenant {TenantId}: {Stored} stored, {Failed} failed, "
            + "{DeletedAtProvider} deleted at the provider. Skipped: {Skipped}.",
            tenantId, report.Stored, report.Failed, report.DeletedAtProvider,
            report.SkippedBecause ?? "no");
    }

    /// <summary>
    /// The sweep, with its collaborators passed in so it can be pinned without a DI container or a
    /// Hangfire storage.
    ///
    /// <para>
    /// <b>The row is created and committed BEFORE the download.</b> The legal basis and the end of
    /// retention are frozen on it at that moment, from the policy as it stands now — so a policy
    /// an institution changes in March cannot silently extend the retention of a recording made in
    /// January. A row created after the bytes landed would leave a file on the volume that nothing
    /// in the database knows about, which is the one state a purge can never reach.
    /// </para>
    /// </summary>
    internal static async Task<RecordingFetchReport> RunAsync(
        IntegrationDbContext db,
        TelephonyAccess access,
        TelephonyRecordingPolicyReader policies,
        IRecordingStore? store,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var report = new RecordingFetchReport();

        var policy = await policies.ReadAsync(tenantId, ct);

        if (policy.Mode == TelRecordingMode.Disabled)
        {
            // Debug and not a warning: this is every tenant until somebody decides otherwise, and
            // a nightly warning per tenant would train everybody to ignore the log.
            logger.LogDebug(
                "Telephony recordings: tenant {TenantId} keeps none.", tenantId);

            return report.Skipped(TelephonyErrors.RecordingDisabled);
        }

        var use = await access.ResolvePortAsync(tenantId, IntegrationCapability.Recordings, ct);

        if (use.IsFailure)
        {
            // The tenant asked for recordings and its provider does not offer them — or telephony
            // was switched off since. A WARNING, unlike the Disabled case: somebody configured a
            // policy that cannot be honoured, and that is worth seeing.
            logger.LogWarning(
                "Telephony recordings: tenant {TenantId} keeps {Mode} but {Code}.",
                tenantId, policy.Mode, use.Code);

            return report.Skipped(use.Code!);
        }

        if (store is null)
        {
            logger.LogError(
                "Telephony recordings: tenant {TenantId} keeps {Mode} but no recording store is "
                + "registered — {Setting} is probably not configured.",
                tenantId, policy.Mode, $"{RecordingStorageOptions.SectionName}:EncryptionKey");

            return report.Skipped("NO_STORE");
        }

        var connectionId = use.Value.Connection.Id;

        // The calls the provider said it has a recording for, which nothing has fetched yet.
        // Narrowed by connection: a tenant that changed provider must not have this sweep ask the
        // new one for the old one's files.
        var pending = await db.TelCalls
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId
                     && c.ConnectionId == connectionId
                     && c.RecordingStatus == TelRecordingStatus.Pending)
            .OrderBy(c => c.StartedAt)
            .Take(MaxPerRun)
            .ToListAsync(ct);

        foreach (var call in pending)
        {
            // The mode's whole meaning lives on the policy, so this job cannot hold a second
            // opinion about what OnDemand means.
            if (!policy.Keeps(call))
            {
                // NotRecorded and not Failed: nothing went wrong. The provider has a file and this
                // institution chose not to keep it, which is a decision rather than an incident —
                // and leaving the call Pending would have every run re-examine it for ever.
                call.SetRecordingStatus(TelRecordingStatus.NotRecorded);
                report.SkippedCalls++;

                continue;
            }

            await FetchOneAsync(db, use.Value, store, policy, clock, logger, call, report, ct);
        }

        await db.SaveChangesAsync(ct);

        return report;
    }

    private static async Task FetchOneAsync(
        IntegrationDbContext db,
        TelephonyPortUse use,
        IRecordingStore store,
        TelRecordingPolicy policy,
        TimeProvider clock,
        ILogger logger,
        TelCall call,
        RecordingFetchReport report,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // The DURATION ceiling, checked before a byte is downloaded. The provider reports it on
        // the call, so this is the cheap refusal — the store's byte ceiling is the backstop for a
        // provider that misreports. A file over the ceiling is Failed and not silently skipped:
        // an operator has to be able to see that a conversation exists and was not kept.
        if (call.TalkSeconds is { } seconds && seconds > policy.MaxDurationSeconds)
        {
            logger.LogWarning(
                "Recording of call {CallId} is {Seconds}s, over the tenant's {Ceiling}s ceiling; "
                + "not fetched.", call.Id, seconds, policy.MaxDurationSeconds);

            call.SetRecordingStatus(TelRecordingStatus.Failed);
            report.Failed++;

            return;
        }

        // Created and committed FIRST — see the method's remarks. The basis and the retention are
        // frozen here from the policy as it stands now.
        var recording = await LoadOrCreateAsync(db, call, policy, now, ct);

        if (recording.Status == TelRecordingStatus.Available)
        {
            // A previous run already stored it and the call's status was not moved. Converge
            // rather than fetch twice.
            call.SetRecordingStatus(TelRecordingStatus.Available);

            return;
        }

        await db.SaveChangesAsync(ct);

        var downloaded = await use.Port.DownloadRecordingAsync(
            new ExternalId(call.ExternalCallId), ct);

        if (downloaded.IsFailure)
        {
            // A RETRYABLE failure leaves everything Pending so the next run tries again; anything
            // else is Failed, because asking the same provider for the same absent file every
            // fifteen minutes for ever is not a retry, it is a loop.
            if (downloaded.IsRetryable)
            {
                logger.LogInformation(
                    "Recording of call {CallId} is not available yet ({Code}); will retry.",
                    call.Id, downloaded.Code);

                return;
            }

            logger.LogWarning(
                "Recording of call {CallId} could not be downloaded ({Family}/{Code}).",
                call.Id, downloaded.Family, downloaded.Code);

            recording.MarkFailed();
            call.SetRecordingStatus(TelRecordingStatus.Failed);
            report.Failed++;

            return;
        }

        try
        {
            var stored = await store.StoreAsync(
                call.TenantId, call.Id, downloaded.Value.Content,
                downloaded.Value.ContentType, ct);

            recording.MarkStored(
                stored.StorageRef,
                Convert.FromHexString(stored.Sha256),
                downloaded.Value.DurationSeconds ?? call.TalkSeconds,
                now);

            call.SetRecordingStatus(TelRecordingStatus.Available);
            report.Stored++;
        }
        catch (DomainException ex)
        {
            // The store refused it — an empty file, an unexpected content type, a file over the
            // byte backstop. Recorded as Failed rather than rethrown: one bad file must not stop
            // the sweep for the twenty-four behind it.
            logger.LogWarning(
                "Recording of call {CallId} was refused by the store: {Reason}",
                call.Id, ex.Message);

            recording.MarkFailed();
            call.SetRecordingStatus(TelRecordingStatus.Failed);
            report.Failed++;

            return;
        }

        // ── The provider's own copy ──────────────────────────────────────────
        // Only when the tenant asked AND the provider can: a provider without RecordingDeletion is
        // an ordinary provider whose retention is its own contract's business.
        if (!policy.DeleteAtProvider) return;

        if (!use.Connection.IsActive) return;

        var deleted = await use.Port.DeleteRecordingAsync(
            new ExternalId(call.ExternalCallId), ct);

        if (deleted.IsSuccess)
        {
            recording.MarkDeletedAtProvider(now);
            report.DeletedAtProvider++;

            return;
        }

        // NOT a failure of the fetch: we hold the file, which is what the institution asked for.
        // The provider keeping its copy is a gap an operator settles with their vendor, and
        // failing here would throw away a recording we successfully stored.
        logger.LogWarning(
            "The provider's copy of call {CallId}'s recording was not deleted ({Family}/{Code}); "
            + "SANKORE holds its own.", call.Id, deleted.Family, deleted.Code);
    }

    /// <summary>
    /// The recording row for this call, created on first sight.
    ///
    /// <para>
    /// <c>ux_tel_recording_call</c> makes it one per call, so a second run finds the row rather
    /// than adding one — which is what lets a crash between the row and the bytes be recoverable
    /// instead of a duplicate.
    /// </para>
    /// </summary>
    private static async Task<TelRecording> LoadOrCreateAsync(
        IntegrationDbContext db,
        TelCall call,
        TelRecordingPolicy policy,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var existing = await db.TelRecordings
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(r => r.TenantId == call.TenantId && r.CallId == call.Id, ct);

        if (existing is not null) return existing;

        // Counted from the CALL and not from the retrieval. A recording fetched three days late
        // because a provider was slow must not be kept three days longer than its neighbours —
        // the retention is about the conversation, not about our plumbing.
        var retentionUntil = call.StartedAt.AddDays(policy.RetentionDays);

        var recording = TelRecording.Expect(
            call.TenantId, call.Id, policy.LegalBasis!, retentionUntil);

        db.TelRecordings.Add(recording);

        return recording;
    }
}

/// <summary>What one fetch run did, for the log line and for the tests.</summary>
internal sealed class RecordingFetchReport
{
    public int Stored { get; set; }

    public int Failed { get; set; }

    public int DeletedAtProvider { get; set; }

    /// <summary>Calls the policy declined to keep. Not a failure — a decision.</summary>
    public int SkippedCalls { get; set; }

    /// <summary>Why nothing was swept, when nothing was.</summary>
    public string? SkippedBecause { get; private set; }

    internal RecordingFetchReport Skipped(string reason)
    {
        SkippedBecause = reason;

        return this;
    }
}
