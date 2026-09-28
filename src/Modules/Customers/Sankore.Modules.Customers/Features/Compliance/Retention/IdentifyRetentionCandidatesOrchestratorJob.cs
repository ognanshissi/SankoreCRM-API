namespace Sankore.Modules.Customers.Features.Compliance.Retention;

using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Global recurring job registered once (not per tenant) and scheduled monthly by
/// <c>Program.cs</c> with the cron <c>0 4 1 * *</c> — 04:00 on the first of the month.
/// <para>
/// It fans out one <see cref="IdentifyRetentionCandidatesJob"/> per active tenant instead of
/// scanning them inline, so a tenant with a slow KYC module cannot hold the scan of every other
/// tenant, and a failure retries that tenant alone.
/// </para>
/// <para>
/// Enqueued arguments are opaque identifiers only (a tenant id) — never an entity, never
/// personal data: a Hangfire payload is persisted in clear in the job store.
/// </para>
/// </summary>
public sealed class IdentifyRetentionCandidatesOrchestratorJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore)
{
    public async Task ExecuteAsync()
    {
        using var scope = scopeFactory.CreateScope();

        var hangfire = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILogger<IdentifyRetentionCandidatesOrchestratorJob>>();

        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        foreach (var tenant in tenants)
        {
            // The SYSTEM placeholder tenant owns no client data; scanning it is meaningless.
            if (tenant.Id == Guid.Empty)
                continue;

            hangfire.Enqueue<IdentifyRetentionCandidatesJob>(job => job.ExecuteAsync(tenant.Id));
        }

        logger.LogInformation(
            "Enqueued the monthly retention scan for {TenantCount} active tenant(s).", tenants.Count);
    }
}
