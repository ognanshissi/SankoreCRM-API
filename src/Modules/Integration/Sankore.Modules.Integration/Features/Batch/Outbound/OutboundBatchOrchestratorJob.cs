namespace Sankore.Modules.Integration.Features.Batch.Outbound;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance, fanning out one
/// <see cref="GenerateTenantOutboundBatchFilesJob"/> per tenant (INT-24, criterion 2: "un job par
/// tenant").
///
/// <para>
/// The shape <c>IntegrationDispatchOrchestratorJob</c>, <c>KycReviewOrchestratorJob</c> and
/// <c>LeadSourcePullOrchestratorJob</c> already use, for the reason they document: one recurring
/// job per tenant would have to be re-registered at every tenant creation and would leave a dead
/// schedule behind at every deactivation.
/// </para>
///
/// <para>
/// <b>Only tenants with an active batch connection are enqueued.</b> On a platform of two hundred
/// tenants of which three exchange files, an unconditional fan-out would put tens of thousands of
/// no-op jobs a day through the shared Hangfire tables — the dashboard becomes unreadable and the
/// enqueue cost dwarfs the work. Each fan-out carries an opaque tenant identifier and nothing
/// else: the Hangfire tables are a shared queue and a dashboard page, and a batch file's contents
/// are a thousand customers' identity data.
/// </para>
/// </summary>
public sealed class OutboundBatchOrchestratorJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<OutboundBatchOrchestratorJob> logger)
{
    /// <summary>
    /// Every five minutes.
    ///
    /// <para>
    /// A cut-off is a time of DAY, per connection, so this sweep's only job is to notice that one
    /// has passed — the cadence is therefore the worst-case lag between the configured hour and
    /// the file being produced. Five minutes is small against a daily cycle and against the hours
    /// a CBS operator allows around a cut-off, while being twelve times cheaper than the
    /// every-minute dispatch sweep, which this does not need to match: nothing here is waiting on
    /// a human.
    /// </para>
    ///
    /// <para>
    /// It is NOT the cut-off hour itself. A single daily schedule would be wrong twice over: the
    /// hour is per connection and per tenant (so there is no one hour to schedule), and a deposit
    /// that failed or a purge that is due must be retried through the day rather than once at
    /// 18:00.
    /// </para>
    /// </summary>
    public const string CronExpression = "*/5 * * * *";

    /// <summary>
    /// Recurring-job id, and the id <c>RecurringJobPauseStore</c> records when an operator pauses
    /// this sweep from the dashboard's Job control page. Kebab-case and nothing else: Hangfire
    /// derives distributed lock names from the id, and dots, colons or a URL in it break them
    /// (repo pitfall).
    /// </summary>
    public const string RecurringJobId = "integration-outbound-batch-orchestrator";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var fannedOut = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expression below: Hangfire serialises the arguments of
            // the lambda, and a captured TenantInfo would ride into a job argument column whole —
            // name, FQDN and connection string included. A local Guid is the only thing that
            // reaches the queue.
            var tenantId = tenant.Id;

            // Guid.Empty is the SYSTEM placeholder, not a tenant. Repo-wide convention.
            if (tenantId == Guid.Empty) continue;

            if (!await HasBatchConnectionAsync(tenantId)) continue;

            hangfire.Enqueue<GenerateTenantOutboundBatchFilesJob>(job => job.ExecuteAsync(tenantId));
            fannedOut++;
        }

        logger.LogDebug(
            "Outbound batch orchestrator: {FannedOut} of {TenantCount} active tenant(s) exchange "
            + "files.", fannedOut, tenants.Count);
    }

    /// <summary>
    /// Whether this tenant has anything for the batch pass to do.
    ///
    /// <para>
    /// The scope is established per tenant and BEFORE it is created:
    /// <see cref="IntegrationDbContext"/> takes an <c>ITenantContext</c> built from the ambient
    /// background context, and a scope opened first would capture the HTTP implementation and find
    /// no request. The query still pairs <c>IgnoreQueryFilters()</c> with an explicit tenant
    /// predicate, as every job in this repo does.
    /// </para>
    /// </summary>
    private async Task<bool> HasBatchConnectionAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        return await HasBatchConnectionAsync(db, tenantId, CancellationToken.None);
    }

    /// <summary>
    /// The predicate on its own, so a test can pin it against an InMemory context without a DI
    /// container — the split <c>IntegrationDispatchOrchestratorJob.OwesWorkAsync</c> uses.
    ///
    /// <para>
    /// It asks only whether a batch connection EXISTS, not whether a command is waiting. A pass
    /// also deposits a file whose transfer failed and purges acknowledged ones, and both are owed
    /// on a day with no new command at all — gating on pending work would quietly stop the purge
    /// on exactly the connections that are idle.
    /// </para>
    /// </summary>
    internal static async Task<bool> HasBatchConnectionAsync(
        IntegrationDbContext db, Guid tenantId, CancellationToken ct)
    {
        // Loads the candidates instead of asking AnyAsync, because the real predicate type-tests
        // the settings and cannot be translated — see OutboundBatchCarrier. The table is per
        // tenant and holds a handful of rows, so this is a cheaper mistake than letting the mode
        // test drift from the dispatcher's again.
        var candidates = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.IsActive
                     && (c.Mode == IntegrationMode.Batch || c.Mode == IntegrationMode.Relay))
            .ToListAsync(ct);

        return candidates.Any(OutboundBatchCarrier.LeavesInAFile);
    }
}
