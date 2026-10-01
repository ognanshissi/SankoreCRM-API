namespace Sankore.Modules.Kyc.Features.Reviews;

using Hangfire;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (KYC-B-07).
///
/// <para>
/// Registering one recurring job per tenant would mean re-registering at every tenant creation and
/// leaving a dead schedule behind at every deactivation. Instead a single daily schedule walks
/// <see cref="ITenantStore.GetAllActiveAsync"/> and fans out one
/// <see cref="ProcessTenantKycReviewsJob"/> per tenant — the same shape M01's nightly orchestrators
/// use.
/// </para>
///
/// <para>
/// Each fan-out carries an opaque tenant identifier and nothing else: the Hangfire tables are a
/// shared queue and a dashboard page, neither of which is a place for KYC evidence. One job per
/// tenant also means one tenant's failure, retry and timing never touch another's.
/// </para>
/// </summary>
public sealed class KycReviewOrchestratorJob(
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<KycReviewOrchestratorJob> logger)
{
    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Id;
            hangfire.Enqueue<ProcessTenantKycReviewsJob>(job => job.ExecuteAsync(tenantId));
        }

        logger.LogInformation(
            "Queued the KYC review sweep for {TenantCount} active tenant(s).", tenants.Count);
    }
}
