namespace Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (cron <c>0 2 * * *</c>).
/// <para>
/// Registering one recurring job per tenant would mean re-registering on every tenant creation;
/// instead a single schedule walks <see cref="ITenantStore.GetAllActiveAsync"/> and fans out one
/// <see cref="DetectDuplicatesJob"/> per tenant. Each fan-out carries opaque identifiers only
/// (the tenant id), and each runs in its own Hangfire job so one slow or failing tenant never
/// blocks the others — retries are per tenant too.
/// </para>
/// </summary>
public sealed class DetectDuplicatesOrchestratorJob(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<DetectDuplicatesOrchestratorJob> logger)
{
    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Id;
            hangfire.Enqueue<DetectDuplicatesJob>(job => job.ExecuteAsync(tenantId));
        }

        logger.LogInformation(
            "Queued duplicate detection for {TenantCount} active tenant(s).", tenants.Count);
    }
}
