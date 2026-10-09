namespace Sankore.Modules.Integration.Features.Sync;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (INT-20, criteria 1
/// and 5).
///
/// <para>
/// It walks <see cref="ITenantStore.GetAllActiveAsync"/> and fans out one
/// <see cref="IntegrationSyncJob"/> per <b>(active connection, due stream)</b>. The shape is
/// <see cref="Dispatch.IntegrationDispatchOrchestratorJob"/>'s, for the same reasons: a recurring
/// job per tenant would have to be registered at every tenant creation and would leave a dead
/// schedule behind at every deactivation.
/// </para>
///
/// <para>
/// <b>Criterion 5 — a failing run does not block the other tenants.</b> Two independent
/// mechanisms, and both are needed. In this job, each tenant's fan-out is wrapped so that an
/// exception — a dropped connection, unreadable settings, a family with no declared streams — is
/// logged and the loop carries on with the next tenant; without it one tenant's bad row would stop
/// the sweep for every tenant after it in the list, silently, since a recurring job that throws is
/// just a red line in a dashboard nobody is watching. And because the fan-out is one Hangfire job
/// per (connection, stream) rather than one per tenant, Hangfire itself isolates the failures: a
/// run that throws retries and fails on its own queue entry and no other tenant's, or indeed no
/// other stream's.
/// </para>
///
/// <para>
/// Each fan-out carries a tenant id, a connection id and a stream name — opaque identifiers and an
/// enum. The Hangfire tables are a shared queue and a dashboard page, and neither is a place for a
/// customer identifier, let alone a payload.
/// </para>
/// </summary>
public sealed class IntegrationSyncOrchestrator(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<IntegrationSyncOrchestrator> logger)
{
    /// <summary>
    /// Every minute — the <b>resolution</b> of the schedule, not the sweep period.
    ///
    /// <para>
    /// The period itself is per stream and per tenant (<see cref="ConnectionSettings.SyncIntervalFor"/>,
    /// default 60 minutes for the screen-facing streams and 240 for history), and this job only
    /// decides which of them are due. It must therefore tick at least as often as the smallest
    /// period an administrator can configure, and that unit is the minute: a cron any slower would
    /// silently round every configured interval up to its own cadence.
    /// </para>
    /// </summary>
    public const string CronExpression = "* * * * *";

    /// <summary>
    /// Recurring-job id, and the id <c>RecurringJobPauseStore</c> records when an operator pauses
    /// this sweep from the dashboard's Job control page. Kebab-case and nothing else: Hangfire
    /// derives distributed lock names from the id, and dots, colons or a URL in it break them
    /// (repo pitfall).
    /// </summary>
    public const string RecurringJobId = "integration-sync-orchestrator";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var fannedOut = 0;
        var failed = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expressions below: Hangfire serialises the arguments of
            // the lambda, and a captured TenantInfo would be serialised whole — the tenant's name,
            // FQDN and connection string riding along in a job argument column.
            var tenantId = tenant.Id;

            // Guid.Empty is the SYSTEM placeholder, not a tenant: it is what background contexts
            // carry when they act for the platform rather than for a customer, and it owns no
            // connection. Repo-wide convention.
            if (tenantId == Guid.Empty) continue;

            try
            {
                fannedOut += await FanOutAsync(tenantId);
            }
            catch (Exception ex)
            {
                // Criterion 5. One tenant's unreadable settings or missing stream declaration must
                // cost that tenant its minute, not everyone else's.
                failed++;
                logger.LogError(
                    ex, "Integration sync: fanning out tenant {TenantId} failed; the remaining "
                      + "tenants are unaffected.", tenantId);
            }
        }

        logger.LogDebug(
            "Integration sync: {FannedOut} (connection, stream) run(s) enqueued across "
            + "{TenantCount} active tenant(s); {Failed} tenant(s) could not be examined.",
            fannedOut, tenants.Count, failed);
    }

    /// <summary>
    /// One tenant's due streams, enqueued.
    ///
    /// <para>
    /// The background scope is established per tenant and BEFORE the DI scope is created:
    /// <see cref="IntegrationDbContext"/> takes an <c>ITenantContext</c> which is built from the
    /// ambient background context, and a scope opened first would capture the HTTP implementation
    /// and find no request. Same rule, same comment, as every job in this repository.
    /// </para>
    /// </summary>
    private async Task<int> FanOutAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var due = await DueStreamsAsync(db, clock.GetUtcNow(), tenantId, CancellationToken.None);

        foreach (var item in due)
        {
            var connectionId = item.ConnectionId;
            var stream = item.Stream;

            hangfire.Enqueue<IntegrationSyncJob>(job => job.ExecuteAsync(tenantId, connectionId, stream));
        }

        return due.Count;
    }

    /// <summary>
    /// Which (connection, stream) pairs of one tenant are due right now.
    ///
    /// <para>
    /// Split out of <see cref="FanOutAsync"/> so a test can pin it against an InMemory context
    /// without a DI container or a Hangfire storage — the split
    /// <c>IntegrationDispatchOrchestratorJob.OwesWorkAsync</c> already uses.
    /// </para>
    ///
    /// <para>
    /// <b>Due-ness is <c>cursor.LastRunAt + interval &lt;= now</c>, and a null
    /// <c>LastRunAt</c> means due.</b> That single rule is also the in-flight lock, which is why
    /// there is no separate "running" flag to maintain: <see cref="IntegrationSyncJob"/> calls
    /// <c>BeginRun</c> and commits it before doing any work, so a run still in flight has pushed
    /// its own <c>LastRunAt</c> to the present and this predicate no longer selects it. The DB row
    /// is the lock, exactly as <c>LeadSourcePullOrchestratorJob</c> uses a <c>Running</c> run row —
    /// and it is self-healing in a way a flag is not: a worker killed mid-run leaves no flag stuck
    /// on, it simply becomes due again one interval later.
    /// </para>
    ///
    /// <para>
    /// <c>IgnoreQueryFilters()</c> paired with an explicit <c>TenantId</c> predicate on both
    /// queries — a job runs outside any HTTP request and the ambient tenant is the argument, not
    /// the context. The cursors are read in ONE query per tenant and matched in memory: six
    /// streams times a handful of connections is a few dozen rows, and a query per pair would be a
    /// few dozen round trips every minute of every day.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<DueSyncStream>> DueStreamsAsync(
        IntegrationDbContext db, DateTimeOffset now, Guid tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var connections = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.IsActive)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

        if (connections.Count == 0) return [];

        var cursors = await db.SyncCursors
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.ConnectionId, c.Stream, c.LastRunAt })
            .ToListAsync(ct);

        var lastRunAt = cursors.ToDictionary(c => (c.ConnectionId, c.Stream), c => c.LastRunAt);

        var due = new List<DueSyncStream>();

        foreach (var connection in connections)
        {
            foreach (var stream in SyncStreams.For(connection.Family))
            {
                // A connection with no settings row at all cannot be asked for its period; the
                // module-wide defaults apply rather than the stream being skipped. "No interval
                // configured" has never meant "never synchronise" — see SyncIntervalMinutes.
                var interval = connection.Settings is { } settings
                    ? settings.SyncIntervalFor(stream)
                    : TimeSpan.FromMinutes(ConnectionSettings.DefaultSyncIntervalMinutes[stream]);

                if (!lastRunAt.TryGetValue((connection.Id, stream), out var lastRun)
                    || lastRun is null
                    || lastRun.Value + interval <= now)
                {
                    due.Add(new DueSyncStream(connection.Id, stream));
                }
            }
        }

        return due;
    }
}

/// <summary>
/// One unit of fan-out: a connection and one of its streams. Deliberately nothing else — what the
/// run needs beyond this it reads from committed state under the tenant it is given.
/// </summary>
internal sealed record DueSyncStream(Guid ConnectionId, SyncStream Stream);
