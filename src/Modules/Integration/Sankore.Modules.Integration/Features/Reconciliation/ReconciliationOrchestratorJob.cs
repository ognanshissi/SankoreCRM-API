namespace Sankore.Modules.Integration.Features.Reconciliation;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (INT-34).
///
/// <para>
/// Same shape as <c>KycLimitWatchOrchestratorJob</c> and for the same reasons: one recurring job
/// per tenant would need re-registering at every tenant creation and would leave a dead schedule
/// behind at every deactivation, so a single schedule walks
/// <see cref="ITenantStore.GetAllActiveAsync"/> and fans out one <see cref="ReconcileTenantJob"/>
/// per tenant. Each fan-out carries an opaque tenant identifier and nothing else — the Hangfire
/// tables are a shared queue and a dashboard page, and neither a customer reference nor a tenant's
/// name has any business in either.
/// </para>
///
/// <para>
/// <b>Only tenants that have something to compare are enqueued.</b> The comparison enumerates the
/// CRM side from <c>integration_reference</c>; a tenant holding no customer reference at all has
/// an empty comparison, and on a platform where a handful of two hundred tenants run a core
/// banking system, fanning out unconditionally would schedule a job a night for each of the rest
/// that opens a run row and reads nothing. WHICH connections are in scope stays the per-tenant
/// job's business — this gate is one indexed existence check, not a second copy of the rule.
/// </para>
/// </summary>
public sealed class ReconciliationOrchestratorJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<ReconciliationOrchestratorJob> logger)
{
    /// <summary>
    /// 06:00 daily — <b>after every other nightly sweep, deliberately</b>. The comparison's whole
    /// value is that it reads state the night has already settled, and each of these can change
    /// what it would conclude:
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>01:00 — M02's periodic KYC review can move a customer's tier, which is one side of
    ///   <c>KycMismatch</c>. Reading it mid-review compares a tier against a tier that is being
    ///   changed.</item>
    /// <item>02:00 — M01's duplicate detection feeds the merges that put a client at status
    ///   <c>Merged</c>, which is what <c>StatusMismatch</c> reports.</item>
    /// <item>03:00–03:30 — M01's segment and loyalty sweeps touch the same client rows the batch
    ///   status read goes through.</item>
    /// <item>05:00 — INT-22's ceiling watch reads the SAME two sides (the snapshot and M02's
    ///   tier). Running after it means a night produces one coherent story rather than two
    ///   readings of the same state taken an hour apart, and the control report is the night's
    ///   last word rather than an intermediate one.</item>
    /// <item>INT-20/21's synchronisation runs continuously (its schedule is a one-minute
    ///   resolution over per-connection intervals), so there is no hour at which the snapshot is
    ///   quiet — but by 06:00 the whole night's worth of refreshes has landed, which is the most a
    ///   schedule can buy.</item>
    /// </list>
    /// <para>
    /// 06:00 is also <b>before the working day</b>: the deployments are West African (UTC+0 in
    /// Côte d'Ivoire, Senegal and Mali), so the internal-control officer finds the summary waiting
    /// when they arrive, over a CRM that no counter has touched since midnight. A comparison run
    /// during opening hours would report divergences an operator closes a minute later, which is
    /// how a ledger stops being believed.
    /// </para>
    /// </remarks>
    public const string CronExpression = "0 6 * * *";

    /// <summary>
    /// Recurring-job id, and the id <c>RecurringJobPauseStore</c> records when an operator pauses
    /// this comparison from the dashboard's Job control page. Kebab-case and nothing else:
    /// Hangfire derives distributed lock names from the id, and dots, colons or a URL in it break
    /// them.
    /// </summary>
    public const string RecurringJobId = "integration-daily-reconciliation";

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
            // reference. Repo-wide convention.
            if (tenantId == Guid.Empty) continue;

            // One tenant's store failing must not cost the others their daily comparison. Without
            // this the loop would abort on the first unreadable tenant and every tenant after it
            // in the list would be silently unreconciled until tomorrow — the fan-out is the only
            // place that can lose the whole platform's night, since the per-tenant jobs are
            // already isolated from each other by Hangfire.
            try
            {
                if (!await HasReferencesAsync(tenantId)) continue;

                hangfire.Enqueue<ReconcileTenantJob>(job => job.ExecuteAsync(tenantId));
                fannedOut++;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "The daily reconciliation could not be scheduled for tenant {TenantId}; the "
                    + "other tenants are unaffected.", tenantId);
            }
        }

        logger.LogDebug(
            "Daily reconciliation: {FannedOut} of {TenantCount} active tenant(s) hold customer "
            + "references to compare.", fannedOut, tenants.Count);
    }

    /// <summary>
    /// Whether this tenant has anything for the comparison to read.
    ///
    /// <para>
    /// The scope is established per tenant and BEFORE the scope is created:
    /// <see cref="IntegrationDbContext"/> takes an <c>ITenantContext</c> built from the ambient
    /// background context, and a scope opened first would capture the HTTP implementation and find
    /// no request.
    /// </para>
    /// </summary>
    private async Task<bool> HasReferencesAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        return await HasReferencesAsync(db, tenantId, CancellationToken.None);
    }

    /// <summary>
    /// The predicate on its own, so a test can pin it against an InMemory context without a DI
    /// container. <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every job
    /// in this repo does — a filter that silently matched the wrong ambient tenant would read
    /// another IMF's references.
    /// </summary>
    internal static Task<bool> HasReferencesAsync(
        IntegrationDbContext db, Guid tenantId, CancellationToken ct)
        => db.References
            .IgnoreQueryFilters()
            .AnyAsync(
                r => r.TenantId == tenantId && r.EntityType == IntegrationEntityTypes.Customer,
                ct);
}
