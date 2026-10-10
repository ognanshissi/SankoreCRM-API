namespace Sankore.Modules.Integration.Features.KycLimits;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (INT-22).
///
/// <para>
/// Same shape as <c>IntegrationDispatchOrchestratorJob</c> and for the same reasons: one recurring
/// job per tenant would need re-registering at every tenant creation and would leave a dead
/// schedule behind at every deactivation, so a single schedule walks
/// <see cref="ITenantStore.GetAllActiveAsync"/> and fans out one <see cref="KycLimitWatchJob"/>
/// per tenant. Each fan-out carries an opaque tenant identifier and nothing else — the Hangfire
/// tables are a shared queue and a dashboard page, and a customer's balance has no business in
/// either.
/// </para>
///
/// <para>
/// <b>Only tenants that have a snapshot to compare are enqueued.</b> The watch reads
/// <c>cbs_customer_snapshot</c>, which exists only for tenants whose CBS connection INT-20/21 has
/// actually synced; on a platform where three tenants of two hundred use a core banking system,
/// fanning out unconditionally would schedule 197 jobs a night that read nothing.
/// </para>
/// </summary>
public sealed class KycLimitWatchOrchestratorJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<KycLimitWatchOrchestratorJob> logger)
{
    /// <summary>
    /// 05:00 daily. Nightly because the ledger bucket is a calendar month and nothing an operator
    /// does with the alert happens faster than a working day; 05:00 because it must read state the
    /// other nightly sweeps have already settled — M02's periodic review at 01:00 can move a
    /// customer's tier, and therefore whether it is capped at all, and INT-20/21's sync is what
    /// puts the night's figures in the snapshot the watch compares. Reading either half-written
    /// would alert on yesterday's balance against yesterday's tier.
    /// </summary>
    public const string CronExpression = "0 5 * * *";

    /// <summary>
    /// Recurring-job id, and the id <c>RecurringJobPauseStore</c> records when an operator pauses
    /// this watch from the dashboard's Job control page. Kebab-case and nothing else: Hangfire
    /// derives distributed lock names from the id, and dots, colons or a URL in it break them.
    /// </summary>
    public const string RecurringJobId = "integration-kyc-limit-watch";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var fannedOut = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expression below: Hangfire serialises the arguments of
            // the lambda, and a captured TenantInfo would be serialised whole — the tenant's name
            // and FQDN riding along in a job argument column. A local Guid is all that reaches the
            // queue.
            var tenantId = tenant.Id;

            // Guid.Empty is the SYSTEM placeholder, not a tenant: it is what background contexts
            // carry when they act for the platform rather than for a customer, and it owns no
            // snapshot. Repo-wide convention.
            if (tenantId == Guid.Empty) continue;

            // One tenant's store failing must not cost the others their night's watch. Without
            // this the loop would abort on the first unreadable tenant and every tenant after it
            // in the list would be silently unwatched until tomorrow — the fan-out is the only
            // place that can hold the whole platform, since the per-tenant jobs are already
            // isolated from each other by Hangfire.
            try
            {
                if (!await HasSnapshotsAsync(tenantId)) continue;

                hangfire.Enqueue<KycLimitWatchJob>(job => job.ExecuteAsync(tenantId));
                fannedOut++;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "KYC ceiling watch could not be scheduled for tenant {TenantId}; the other "
                    + "tenants are unaffected.", tenantId);
            }
        }

        logger.LogDebug(
            "KYC ceiling watch: {FannedOut} of {TenantCount} active tenant(s) have a core-banking "
            + "snapshot to compare.", fannedOut, tenants.Count);
    }

    /// <summary>
    /// Whether this tenant has anything for the watch to read.
    ///
    /// <para>
    /// The scope is established per tenant and BEFORE it is created:
    /// <see cref="IntegrationDbContext"/> takes an <c>ITenantContext</c> built from the ambient
    /// background context, and a scope opened first would capture the HTTP implementation and find
    /// no request.
    /// </para>
    /// </summary>
    private async Task<bool> HasSnapshotsAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        return await HasSnapshotsAsync(db, tenantId, CancellationToken.None);
    }

    /// <summary>
    /// The predicate on its own, so a test can pin it against an InMemory context without a DI
    /// container. <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every job
    /// in this repo does — a filter that silently matched the wrong ambient tenant would read
    /// another IMF's customers.
    /// </summary>
    internal static Task<bool> HasSnapshotsAsync(
        IntegrationDbContext db, Guid tenantId, CancellationToken ct)
        => db.CbsSnapshots
            .IgnoreQueryFilters()
            .AnyAsync(s => s.TenantId == tenantId, ct);
}
