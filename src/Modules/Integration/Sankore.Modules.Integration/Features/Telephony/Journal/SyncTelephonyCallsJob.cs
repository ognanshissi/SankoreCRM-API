namespace Sankore.Modules.Integration.Features.Telephony.Journal;

using System.Globalization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Finds the calls whose webhook was lost (TEL-B-14).
///
/// <para>
/// <b>Why a sweep exists at all when there is a webhook.</b> A webhook is one HTTP request from
/// another company's platform to ours: it is lost to a deployment of ours, a network partition, a
/// rate limit, an expired certificate, a provider incident. Every one of those is a call missing
/// from the journal — and missing silently, because nothing in the platform knows an event it
/// never received. The sweep asks the provider directly, which is the only question whose answer
/// does not depend on a delivery having worked.
/// </para>
///
/// <para>
/// <b>It runs on the existing sync machinery and not on a schedule of its own.</b>
/// <c>SyncStream.Calls</c> and its fifteen-minute default were declared in Lot 1, so
/// <c>IntegrationSyncOrchestrator</c> already fans this out per (connection, stream) with the
/// in-flight lock, the per-tenant isolation and the cursor row — which is also why Lot 1's
/// declaration was not merely preparatory: until this job existed the orchestrator dispatched the
/// Calls stream to <c>IntegrationSyncJob</c>, which reads <c>integration_reference</c> for
/// CUSTOMERS, finds none on a telephony connection and advanced the cursor having done nothing.
/// Harmless, and fifteen minutes of nothing every fifteen minutes.
/// </para>
///
/// <para>
/// <b>On the <c>integration-sync</c> queue</b>, with the other sweeps: it reads the same
/// provider's per-minute ceiling as the webhook processing, and an operator draining one has to
/// be able to drain the other with it.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class SyncTelephonyCallsJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// <c>IntegrationSyncJob.QueueName</c>, repeated as a literal because a <c>[Queue]</c>
    /// attribute takes a compile-time constant. <b>The host must run a worker on this queue</b> —
    /// a job enqueued to a queue nobody reads never runs, and nothing says so.
    /// </summary>
    private const string QueueName = "integration-sync";

    /// <summary>
    /// How far back of already-swept window to re-read on every run.
    ///
    /// <para>
    /// Ten minutes, as the story asks, and the reason is the provider's own clock rather than
    /// ours: a call is listed by the instant it STARTED, and a call that started at 10:29 and
    /// ended at 10:34 may not appear in a listing taken at 10:30 — or may appear with a status
    /// that is not final. A cursor advanced to the start of the run would step over exactly those.
    /// Re-reading is free: the journal converges on one row per provider call id.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What the first run of a connection reads, having no cursor.
    ///
    /// <para>
    /// One hour and not "everything": a tenant switching telephony on does not want its first
    /// sweep to import a year of calls nobody in the CRM can attach to anything, and the webhook
    /// is what covers calls from the moment the connection went live. A deliberate import of
    /// history is a different feature and nobody has asked for one.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan FirstRunLookback = TimeSpan.FromHours(1);

    /// <summary>
    /// Pages fetched per run. A ceiling and not a target: it bounds what one run can cost the
    /// provider's rate limit, and the next run picks up where this one stopped because the cursor
    /// only advances on a complete pass.
    /// </summary>
    internal const int MaxPagesPerRun = 20;

    public async Task ExecuteAsync(Guid tenantId, Guid connectionId)
    {
        // Set BEFORE the scope is created: ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them. SYSTEM — an interval elapsed.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<SyncTelephonyCallsJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<TelephonyAccess>(),
            sp.GetRequiredService<TelCallJournalWriter>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            connectionId,
            CancellationToken.None);

        logger.LogInformation(
            "Telephony catch-up for tenant {TenantId} connection {ConnectionId}: {Journalled} "
            + "call(s) read over {Pages} page(s). Skipped: {Skipped}.",
            tenantId, connectionId, report.Journalled, report.Pages, report.SkippedBecause ?? "no");
    }

    /// <summary>
    /// The sweep, with its collaborators passed in so the ordering below can be pinned without a
    /// DI container or a Hangfire storage.
    ///
    /// <para>
    /// <b>The cursor advances only after the calls are committed, in a SECOND transaction.</b>
    /// Exactly the order <c>IntegrationSyncJob</c> states and for the same reason: the opposite —
    /// or one transaction saving both — loses a window of calls on any crash and loses it
    /// SILENTLY, because nothing afterwards knows they were skipped. A crash between the two
    /// re-reads a window already journalled, which costs nothing because the journal converges on
    /// one row per provider call id.
    /// </para>
    ///
    /// <para>
    /// <b>The cursor is advanced to the instant the run STARTED minus the overlap</b>, not to the
    /// instant it finished. See <see cref="Overlap"/>.
    /// </para>
    /// </summary>
    internal static async Task<TelephonyCatchUpReport> RunAsync(
        IntegrationDbContext db,
        TelephonyAccess access,
        TelCallJournalWriter journal,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid connectionId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        var report = new TelephonyCatchUpReport();
        var startedAt = clock.GetUtcNow();

        var use = await access.ResolvePortAsync(tenantId, IntegrationCapability.CallHistory, ct);

        if (use.IsFailure)
        {
            // Telephony switched off, or a provider with no call-history API — in which case the
            // webhook is all there is and this sweep is correctly a no-op. The cursor is left
            // completely untouched: a run that did not happen must not look like one that did.
            logger.LogDebug(
                "Telephony catch-up skipped for tenant {TenantId}: {Code}.", tenantId, use.Code);

            return report.Skipped(use.Code!);
        }

        // The connection resolved by the family may not be the one the orchestrator fanned out —
        // a tenant that replaced its provider leaves the old row inactive, and the fan-out is
        // per connection. Sweeping the wrong one would journal its calls under this cursor.
        if (use.Value.Connection.Id != connectionId)
        {
            logger.LogInformation(
                "Telephony catch-up skipped for tenant {TenantId}: connection {ConnectionId} is "
                + "no longer the active telephony connection.", tenantId, connectionId);

            return report.Skipped("NOT_THE_ACTIVE_CONNECTION");
        }

        var cursor = await SyncCursorGate.LoadOrCreateAsync(
            db, tenantId, connectionId, SyncStream.Calls, ct);

        var from = ReadFrom(cursor, startedAt);

        cursor.BeginRun(clock);
        await db.SaveChangesAsync(ct);

        try
        {
            string? page = null;

            for (var i = 0; i < MaxPagesPerRun; i++)
            {
                var calls = await use.Value.Port.ListCallsAsync(from, page, ct);

                if (calls.IsFailure)
                {
                    // Thrown so the catch below records it and the cursor keeps its value: this is
                    // the one path that must re-read, and swallowing it would advance past calls
                    // the provider never gave us.
                    throw new InvalidOperationException(
                        $"The provider could not list calls ({calls.Family}/{calls.Code}).");
                }

                report.Pages++;

                foreach (var call in calls.Value.Calls)
                {
                    // The same writer the webhook uses — ONE definition of insert-or-update, of
                    // which transitions are allowed and of who may overwrite whose attachment. A
                    // sweep with its own copy is a sweep that eventually undoes an agent's
                    // correction.
                    //
                    // The reading's StatusChanged is DISCARDED here, and that is a decision
                    // rather than an oversight: this sweep does not push the realtime events of
                    // TEL-B-10. It runs every quarter of an hour, so a transition it discovers is
                    // up to fifteen minutes old — and opening an agent's call panel for a
                    // conversation that finished a quarter of an hour ago is worse than not
                    // opening it at all. The webhook is the live path; this one is the journal's
                    // net, and its job is that the row exists, not that a screen reacted.
                    await journal.RecordAsync(tenantId, connectionId, call, ct);
                    report.Journalled++;
                }

                page = calls.Value.NextCursor;

                if (string.IsNullOrWhiteSpace(page)) break;
            }

            // ── The DATA commit. ────────────────────────────────────────────
            await db.SaveChangesAsync(ct);

            // ── And only now the cursor, in its own transaction. ────────────
            cursor.Advance(
                (startedAt - Overlap).ToString("O", CultureInfo.InvariantCulture), clock);

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            report.Failed = true;

            logger.LogError(
                ex, "Telephony catch-up for tenant {TenantId} connection {ConnectionId} failed "
                  + "after {Journalled} call(s); the cursor is unchanged and the window will be "
                  + "re-read.", tenantId, connectionId, report.Journalled);

            // Everything the failed sweep had queued — half-written calls, an Advance that may
            // already be staged — must not ride along into the failure write below, which is its
            // own transaction.
            db.ChangeTracker.Clear();

            try
            {
                await SyncCursorGate.RecordFailureAsync(
                    db, clock, tenantId, connectionId, SyncStream.Calls, ex.Message, ct);
            }
            catch (Exception recordingEx)
            {
                logger.LogError(
                    recordingEx,
                    "Recording the failure of the telephony catch-up for tenant {TenantId} also "
                    + "failed.", tenantId);
            }
        }

        return report;
    }

    /// <summary>
    /// Where this run starts reading.
    ///
    /// <para>
    /// A cursor this module cannot parse is treated as absent rather than as an error: the column
    /// is documented opaque, and a run that refused to start because of one would stop the sweep
    /// for ever on a value nobody can see. The lookback then applies, which re-reads an hour — the
    /// cheap direction.
    /// </para>
    /// </summary>
    internal static DateTimeOffset ReadFrom(IntegrationSyncCursor cursor, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        if (!string.IsNullOrWhiteSpace(cursor.Cursor)
            && DateTimeOffset.TryParse(
                cursor.Cursor, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return startedAt - FirstRunLookback;
    }
}

/// <summary>What one catch-up run did, for the log line and for the tests.</summary>
internal sealed class TelephonyCatchUpReport
{
    public int Journalled { get; set; }

    public int Pages { get; set; }

    /// <summary>The sweep threw. The cursor kept its value and the error was recorded.</summary>
    public bool Failed { get; set; }

    /// <summary>Why nothing was swept, when nothing was. Null when the run did work.</summary>
    public string? SkippedBecause { get; private set; }

    internal TelephonyCatchUpReport Skipped(string reason)
    {
        SkippedBecause = reason;

        return this;
    }
}
