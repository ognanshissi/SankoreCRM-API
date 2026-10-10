namespace Sankore.Modules.Integration.Features.Dispatch;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire recurring job — GLOBAL, one registration for the whole instance (INT-06, criterion 1).
///
/// <para>
/// One recurring job per tenant would have to be re-registered at every tenant creation and would
/// leave a dead schedule behind at every deactivation. Instead a single schedule walks
/// <see cref="ITenantStore.GetAllActiveAsync"/> and fans out one
/// <see cref="DispatchTenantCommandsJob"/> per tenant — the shape
/// <c>KycReviewOrchestratorJob</c> and <c>LeadSourcePullOrchestratorJob</c> already use.
/// </para>
///
/// <para>
/// <b>Only tenants that actually owe work are enqueued.</b> The criterion says "one job per tenant
/// having Pending or due RetryScheduled commands", and the difference is not cosmetic: this job
/// runs every minute, and on a platform with two hundred tenants of which three use a CBS, fanning
/// out unconditionally would put 288 000 no-op jobs a day through the shared Hangfire tables — the
/// dashboard becomes unreadable and the enqueue cost dwarfs the work.
/// </para>
///
/// <para>
/// Each fan-out carries an opaque tenant identifier and nothing else (criterion 2). The Hangfire
/// tables are a shared queue and a dashboard page, and neither is a place for a customer's
/// identity document — which is what an integration command's payload is. One job per tenant also
/// means one tenant's failure, retry and timing never touch another's.
/// </para>
/// </summary>
public sealed class IntegrationDispatchOrchestratorJob(
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    IBackgroundJobClient hangfire,
    ILogger<IntegrationDispatchOrchestratorJob> logger)
{
    /// <summary>
    /// Every minute. A command's whole promise to a consumer module is that an agent does not wait
    /// for a system SANKORE does not operate, so the sweep's cadence is the platform's answer to
    /// "how late is a write" — and the backoff curve starts at thirty seconds, which a cadence
    /// slower than a minute would round up.
    /// </summary>
    public const string CronExpression = "* * * * *";

    /// <summary>
    /// Recurring-job id, and the id <c>RecurringJobPauseStore</c> records when an operator pauses
    /// this sweep from the dashboard's Job control page. Kebab-case and nothing else: Hangfire
    /// derives distributed lock names from the id, and dots, colons or a URL in it break them
    /// (repo pitfall). Same shape as <c>"lead-source-pull-orchestrator"</c>.
    /// </summary>
    public const string RecurringJobId = "integration-dispatch-orchestrator";

    public async Task ExecuteAsync()
    {
        var tenants = await tenantStore.GetAllActiveAsync(CancellationToken.None);

        var fannedOut = 0;

        foreach (var tenant in tenants)
        {
            // Hoisted out of the Hangfire expression below. Hangfire serialises the arguments of
            // the lambda, and a captured loop variable of type TenantInfo would be serialised
            // whole — the tenant's name, FQDN and connection string riding along in a job
            // argument column. A local Guid is the only thing that reaches the queue.
            var tenantId = tenant.Id;

            // Guid.Empty is the SYSTEM placeholder, not a tenant: it is what background contexts
            // carry when they act for the platform rather than for a customer, and it owns no
            // commands. Repo-wide convention — the module consumers skip it too.
            if (tenantId == Guid.Empty) continue;

            if (!await OwesWorkAsync(tenantId)) continue;

            hangfire.Enqueue<DispatchTenantCommandsJob>(job => job.ExecuteAsync(tenantId));
            fannedOut++;
        }

        logger.LogDebug(
            "Integration dispatch: {FannedOut} of {TenantCount} active tenant(s) owed work.",
            fannedOut, tenants.Count);
    }

    /// <summary>
    /// Whether this tenant has at least one command the dispatcher may pick up now.
    ///
    /// <para>
    /// The scope is established per tenant and BEFORE it is created:
    /// <see cref="IntegrationDbContext"/> takes an <c>ITenantContext</c>, which is built from the
    /// ambient background context, and a scope opened first would capture the HTTP implementation
    /// and find no request. The query itself still pairs <c>IgnoreQueryFilters()</c> with an
    /// explicit tenant predicate, as every job in this repo does — a filter that silently matched
    /// the wrong ambient tenant would read another IMF's queue.
    /// </para>
    ///
    /// <para>
    /// The predicate MIRRORS <see cref="IntegrationCommand.IsDueAt"/> and the two must keep
    /// agreeing; it cannot call it, because an instance method does not translate to SQL. The
    /// per-tenant job re-checks with <c>IsDueAt</c>, which stays the authority.
    /// </para>
    /// </summary>
    private async Task<bool> OwesWorkAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        return await OwesWorkAsync(db, clock.GetUtcNow(), tenantId, CancellationToken.None);
    }

    /// <summary>
    /// The "owes work" predicate on its own, so a test can pin it against an InMemory context
    /// without a DI container — the same split <c>ProcessTenantKycReviewsJob.RunAsync</c> uses.
    /// </summary>
    internal static Task<bool> OwesWorkAsync(
        IntegrationDbContext db, DateTimeOffset now, Guid tenantId, CancellationToken ct)
        => db.Commands
            .IgnoreQueryFilters()
            .AnyAsync(
                c => c.TenantId == tenantId
                  && (c.Status == CommandStatus.Pending
                      || (c.Status == CommandStatus.RetryScheduled
                          && (c.NextAttemptAt == null || c.NextAttemptAt <= now))),
                ct);
}
