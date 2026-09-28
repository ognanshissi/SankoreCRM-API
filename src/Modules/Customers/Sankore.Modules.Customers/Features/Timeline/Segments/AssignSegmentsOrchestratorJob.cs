namespace Sankore.Modules.Customers.Features.Timeline.Segments;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job, GLOBAL (one registration, all tenants) — cron <c>0 3 * * *</c>.
///
/// It only fans out: one <see cref="AssignSegmentsJob"/> per active tenant, enqueued as a
/// separate background job. Doing the work inline would mean one slow tenant delays every
/// other, and a single failure loses the whole night's run; as separate jobs each tenant
/// retries on its own.
/// </summary>
public sealed class AssignSegmentsOrchestratorJob(
    ITenantStore tenantStore,
    IBackgroundJobClient backgroundJobs,
    ILogger<AssignSegmentsOrchestratorJob> logger)
{
    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
            backgroundJobs.Enqueue<AssignSegmentsJob>(job => job.ExecuteAsync(tenant.Id));

        logger.LogInformation(
            "Enqueued client segmentation for {TenantCount} active tenant(s).", tenants.Count);
    }
}
