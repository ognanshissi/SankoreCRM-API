namespace Sankore.Modules.Customers.Features.Timeline.Loyalty;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job, GLOBAL — cron <c>30 3 * * *</c>, i.e. half an hour after the
/// segmentation orchestrator, so tonight's scores are computed against tonight's segments.
///
/// Fans out one <see cref="ComputeLoyaltyScoresJob"/> per active tenant; the arguments are
/// opaque identifiers only.
/// </summary>
public sealed class ComputeLoyaltyScoresOrchestratorJob(
    ITenantStore tenantStore,
    IBackgroundJobClient backgroundJobs,
    ILogger<ComputeLoyaltyScoresOrchestratorJob> logger)
{
    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
            backgroundJobs.Enqueue<ComputeLoyaltyScoresJob>(job => job.ExecuteAsync(tenant.Id));

        logger.LogInformation(
            "Enqueued loyalty scoring for {TenantCount} active tenant(s).", tenants.Count);
    }
}
