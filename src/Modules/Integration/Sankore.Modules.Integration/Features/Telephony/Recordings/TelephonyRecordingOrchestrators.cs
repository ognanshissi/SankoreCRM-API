namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (TEL-B-16, F12.15).
///
/// <para>
/// Fans out one <see cref="FetchTelephonyRecordingsJob"/> per active tenant. The shape every
/// orchestrator in this repository has, for the reasons <c>IntegrationSyncOrchestrator</c> states:
/// a recurring job per tenant would have to be registered at every tenant creation and would
/// leave a dead schedule behind at every deactivation, and one Hangfire job per tenant is what
/// makes one tenant's failure cost that tenant its run rather than everybody's.
/// </para>
///
/// <para>
/// It fans out for EVERY tenant and not only for those that record, deliberately: deciding here
/// would mean reading every tenant's policy from the orchestrator's own scope, and the job already
/// reads it as its first act and skips in a line. The cost is one no-op job per tenant per run;
/// the alternative is a policy read in two places that can disagree.
/// </para>
/// </summary>
public sealed class FetchTelephonyRecordingsOrchestrator(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<FetchTelephonyRecordingsOrchestrator> logger)
{
    /// <summary>
    /// Every fifteen minutes, the cadence of the catch-up sweep — a recording is not ready the
    /// instant a call ends, and an agent reviewing a conversation an hour later is the use case.
    /// Not every minute: each run can move hundreds of megabytes.
    /// </summary>
    public const string CronExpression = "*/15 * * * *";

    /// <summary>
    /// Kebab-case and nothing else: Hangfire derives distributed lock names from the id, and a
    /// dot, a colon or a URL in one breaks them.
    /// </summary>
    public const string RecurringJobId = "integration-telephony-recording-fetch";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var enqueued = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expression: Hangfire serialises the lambda's arguments,
            // and a captured TenantInfo would put the tenant's name, FQDN and connection string in
            // a job-argument column and on a dashboard page.
            var tenantId = tenant.Id;

            if (tenantId == Guid.Empty) continue;

            hangfire.Enqueue<FetchTelephonyRecordingsJob>(job => job.ExecuteAsync(tenantId));
            enqueued++;
        }

        logger.LogDebug(
            "Telephony recording fetch: {Enqueued} tenant run(s) enqueued.", enqueued);
    }
}

/// <summary>
/// Hangfire recurring job — GLOBAL (TEL-B-17). Fans out one
/// <see cref="PurgeTelephonyRecordingsJob"/> per active tenant.
///
/// <para>
/// <b>Its own orchestrator rather than a second act of the fetch one.</b> They run on different
/// cadences for different reasons — fetching chases a provider every quarter of an hour, purging
/// is a daily retention duty — and an operator pausing one from the dashboard's Job control page
/// must not be pausing the other. Pausing the fetch loses recordings an institution wanted;
/// pausing the purge keeps recordings it is obliged to destroy.
/// </para>
/// </summary>
public sealed class PurgeTelephonyRecordingsOrchestrator(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<PurgeTelephonyRecordingsOrchestrator> logger)
{
    /// <summary>
    /// 03:15 daily, in the quiet window the module's other maintenance uses — after the partition
    /// job at 02:00 and the link check at 02:40, and before M13's lead anonymisation at 03:45.
    /// </summary>
    public const string CronExpression = "15 3 * * *";

    public const string RecurringJobId = "integration-telephony-recording-purge";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var enqueued = 0;

        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Id;

            if (tenantId == Guid.Empty) continue;

            hangfire.Enqueue<PurgeTelephonyRecordingsJob>(job => job.ExecuteAsync(tenantId));
            enqueued++;
        }

        logger.LogDebug(
            "Telephony recording purge: {Enqueued} tenant run(s) enqueued.", enqueued);
    }
}
