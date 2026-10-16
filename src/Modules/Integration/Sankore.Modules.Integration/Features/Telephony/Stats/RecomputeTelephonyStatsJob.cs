namespace Sankore.Modules.Integration.Features.Telephony.Stats;

using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire job — one tenant's indicators, recomputed over a trailing window (TEL-B-19).
///
/// <para>
/// <b>One job, two schedules, and the second one is not redundant.</b> The hourly run covers the
/// two days anybody is looking at, so a missed call appears within the hour without the journal
/// paying for a recomputation per webhook. The nightly run covers a week, because the things that
/// change a figure AFTER the day are not events this module sees in time: a supervisor correcting
/// an attribution, a client anonymised, an outcome whose "counts as contact" flag was wrong all
/// along. Without the second schedule those corrections would never reach the read model, and the
/// word "recalculable" in TEL-B-19 would describe a capability nobody exercises.
/// </para>
/// </summary>
public sealed class RecomputeTelephonyStatsJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// How far back each schedule reaches. Two days for the hourly one so the UTC day boundary is
    /// never a gap, seven for the nightly one.
    /// </summary>
    public const int HourlyDays = 2;

    public const int NightlyDays = 7;

    public async Task ExecuteAsync(Guid tenantId, int days)
    {
        // Set BEFORE the scope: ITenantContext and ICurrentUser are built from this ambient
        // context when the scope resolves them. SYSTEM is the actor — a schedule elapsed.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<RecomputeTelephonyStatsJob>>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = today.AddDays(-(Math.Max(days, 1) - 1));

        var report = await TelCallStatsRecomputer.RecomputeRangeAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            clock,
            logger,
            tenantId,
            from,
            today,
            CancellationToken.None);

        // Debug and not information: this runs 24 times a day per tenant and says nothing an
        // operator acts on. The refusal above logs a warning on its own.
        logger.LogDebug(
            "Telephony stats for tenant {TenantId}: {Days} day(s) recomputed, {Buckets} bucket(s).",
            tenantId, report.Days, report.Buckets);
    }
}

/// <summary>
/// Hangfire recurring job — GLOBAL, hourly (TEL-B-19). Fans out one
/// <see cref="RecomputeTelephonyStatsJob"/> per active tenant over the last two days.
/// </summary>
public sealed class RecomputeTelephonyStatsHourlyOrchestrator(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<RecomputeTelephonyStatsHourlyOrchestrator> logger)
{
    /// <summary>
    /// Twenty past the hour, not on the hour: the platform's other hourly work starts at :00 and
    /// a recomputation is the least urgent thing in the queue.
    /// </summary>
    public const string CronExpression = "20 * * * *";

    public const string RecurringJobId = "integration-telephony-stats-hourly";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var enqueued = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expression: a captured TenantInfo would put the tenant's
            // name, FQDN and connection string in a job-argument column and on a dashboard page.
            var tenantId = tenant.Id;

            if (tenantId == Guid.Empty) continue;

            hangfire.Enqueue<RecomputeTelephonyStatsJob>(
                job => job.ExecuteAsync(tenantId, RecomputeTelephonyStatsJob.HourlyDays));

            enqueued++;
        }

        logger.LogDebug("Telephony stats (hourly): {Enqueued} tenant run(s) enqueued.", enqueued);
    }
}

/// <summary>
/// Hangfire recurring job — GLOBAL, nightly (TEL-B-19). The same job over a seven-day window, so
/// corrections made after the fact reach the read model.
/// </summary>
public sealed class RecomputeTelephonyStatsNightlyOrchestrator(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<RecomputeTelephonyStatsNightlyOrchestrator> logger)
{
    /// <summary>
    /// 04:10, AFTER M13's lead anonymisation at 03:45 and this module's recording purge at 03:15
    /// — both of them change calls, and a recomputation that ran first would be recomputing the
    /// state they are about to leave behind.
    /// </summary>
    public const string CronExpression = "10 4 * * *";

    public const string RecurringJobId = "integration-telephony-stats-nightly";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var enqueued = 0;

        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Id;

            if (tenantId == Guid.Empty) continue;

            hangfire.Enqueue<RecomputeTelephonyStatsJob>(
                job => job.ExecuteAsync(tenantId, RecomputeTelephonyStatsJob.NightlyDays));

            enqueued++;
        }

        logger.LogDebug("Telephony stats (nightly): {Enqueued} tenant run(s) enqueued.", enqueued);
    }
}
