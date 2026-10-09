namespace Sankore.Modules.Integration.Features.Batch.Inbound;

using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (INT-25).
///
/// <para>
/// Same shape as <c>IntegrationDispatchOrchestratorJob</c> and <c>KycLimitWatchOrchestratorJob</c>,
/// and for the same reasons: one recurring job per tenant would need re-registering at every
/// tenant creation and would leave a dead schedule behind at every deactivation, so a single
/// schedule walks <see cref="ITenantStore.GetAllActiveAsync"/> and fans out one
/// <see cref="PollInboundBatchFilesJob"/> per tenant. Each fan-out carries an opaque tenant
/// identifier and nothing else — the Hangfire tables are a shared queue and a dashboard page, and
/// an SFTP host, a directory or a customer's external id has no business in either.
/// </para>
///
/// <para>
/// <b>Only tenants that run a file-based connection are enqueued.</b> On a platform of two hundred
/// tenants of which three exchange files, fanning out unconditionally would schedule 197 jobs an
/// hour that open nothing — and each of those jobs would still resolve a transport and a
/// projector. <see cref="InboundBatchTargets"/> answers the question once, for both jobs.
/// </para>
/// </summary>
public sealed class InboundBatchPollOrchestratorJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<InboundBatchPollOrchestratorJob> logger)
{
    /// <summary>
    /// Every fifteen minutes, on the quarter.
    ///
    /// <para>
    /// A batch connection is a daily rhythm — <c>BatchCapableSettings.CutOffTime</c> defaults to
    /// 18:00 — so the inbound file of a given day appears once, at an hour neither side controls
    /// precisely: a CBS that runs its own batch "overnight" may deposit at 02:10 or at 05:40. The
    /// cadence therefore has to be a polling interval and not an appointment, and fifteen minutes
    /// is where the two costs cross: it bounds how late a day's closures are by a quarter of an
    /// hour, while opening four SFTP sessions an hour per connection rather than sixty. A
    /// per-minute sweep — the dispatcher's cadence — would spend its day listing an empty
    /// directory, and criterion 3's delay is measured in HOURS, so nothing here is made more
    /// correct by running sooner.
    /// </para>
    /// </summary>
    public const string CronExpression = "*/15 * * * *";

    /// <summary>
    /// Recurring-job id, and the id <c>RecurringJobPauseStore</c> records when an operator pauses
    /// this sweep from the dashboard's Job control page. Kebab-case and nothing else: Hangfire
    /// derives distributed lock names from the id, and dots, colons or a URL in it break them
    /// (repo pitfall).
    /// </summary>
    public const string RecurringJobId = "integration-inbound-batch-poll";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var fannedOut = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expression below: Hangfire serialises the arguments of
            // the lambda, and a captured TenantInfo would be serialised whole — the tenant's name,
            // FQDN and connection string riding along in a job argument column. A local Guid is
            // all that reaches the queue.
            var tenantId = tenant.Id;

            // Guid.Empty is the SYSTEM placeholder, not a tenant: it is what background contexts
            // carry when they act for the platform rather than for a customer, and it owns no
            // connection. Repo-wide convention.
            if (tenantId == Guid.Empty) continue;

            // One tenant's store failing must not cost the others their sweep. Without this the
            // loop would abort on the first unreadable tenant and every tenant after it would go
            // unpolled until the next quarter — the fan-out is the only place that can hold the
            // whole platform, since the per-tenant jobs are already isolated by Hangfire.
            try
            {
                if (!await HasFileConnectionAsync(tenantId)) continue;

                hangfire.Enqueue<PollInboundBatchFilesJob>(job => job.ExecuteAsync(tenantId));
                fannedOut++;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "The inbound batch sweep could not be scheduled for tenant {TenantId}; the "
                    + "other tenants are unaffected.", tenantId);
            }
        }

        logger.LogDebug(
            "Inbound batch poll: {FannedOut} of {TenantCount} active tenant(s) run a file-based "
            + "connection.", fannedOut, tenants.Count);
    }

    /// <summary>
    /// Whether this tenant has anything for the sweep to do.
    ///
    /// <para>
    /// The scope is established per tenant and BEFORE it is created:
    /// <see cref="IntegrationDbContext"/> takes an <c>ITenantContext</c> built from the ambient
    /// background context, and a scope opened first would capture the HTTP implementation and find
    /// no request.
    /// </para>
    /// </summary>
    private async Task<bool> HasFileConnectionAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        return await HasFileConnectionAsync(db, tenantId, CancellationToken.None);
    }

    /// <summary>
    /// The predicate on its own, so a test can pin it against an InMemory context without a DI
    /// container — the same split every orchestrator of this module uses.
    /// </summary>
    internal static async Task<bool> HasFileConnectionAsync(
        IntegrationDbContext db, Guid tenantId, CancellationToken ct)
        => (await InboundBatchTargets.ListAsync(db, tenantId, ct)).Count > 0;
}
