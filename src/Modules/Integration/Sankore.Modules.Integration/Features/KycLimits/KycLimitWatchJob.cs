namespace Sankore.Modules.Integration.Features.KycLimits;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Hangfire job — <b>the simplified-KYC ceiling watch of ONE tenant (INT-22)</b>. Enqueued by
/// <see cref="KycLimitWatchOrchestratorJob"/>; its only argument is an opaque tenant identifier.
///
/// <para>
/// It compares each <see cref="CbsSnapshot"/>'s <see cref="CbsSnapshot.TotalBalance"/> and
/// <see cref="CbsSnapshot.MonthlyFlow"/> to the ceilings M02 holds for that customer, publishes
/// <c>KycLimitApproaching</c> at the alert threshold and <c>KycLimitExceeded</c> on breach, and
/// records each one in the <see cref="KycLimitAlert"/> ledger so it is said once per customer and
/// per month.
/// </para>
///
/// <para>
/// <b>On the named queue <c>integration-sync</c></b>, not <c>integration-write</c>: the watch
/// reads the read model INT-21 refreshes and makes no outbound call at all, so it has no business
/// competing for the worker pool that is rationed by a CBS licence. <b>The host must declare that
/// queue</b> — <c>DispatchServiceRegistration</c> carries the <c>BackgroundJobServerOptions</c>,
/// and without it the job is enqueued to a queue no worker reads and never runs.
/// </para>
///
/// <para>
/// <b>It never reads the tier the CBS believes.</b> <see cref="CbsSnapshot.KycLevelInCbs"/> is
/// right there in the row, and filtering on it would save an <see cref="IKycModule"/> call per
/// customer — but the two sides are allowed to disagree (that disagreement is INT-21's
/// <c>CbsKycMismatchDetectedEvent</c>, reported and not corrected), and whether a customer is
/// capped is a compliance fact owned by M02. A CBS that says "full" about a customer M02 holds at
/// simplified would silently switch the watch off for exactly the customer it exists for.
/// </para>
/// </summary>
[Queue(QueueName)]
public sealed class KycLimitWatchJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// The read queue of the module, declared by <c>DispatchServiceRegistration.Queues</c>.
    /// </summary>
    public const string QueueName = "integration-sync";

    public async Task ExecuteAsync(Guid tenantId)
    {
        // Set BEFORE the scope is created. ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a scope opened first would capture the
        // HTTP implementations and find no request — same comment, same reason, as
        // DispatchTenantCommandsJob. The actor is SYSTEM: a timestamp decided that a ceiling was
        // near, not an agent.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILogger<KycLimitWatchJob>>();

        var report = await RunAsync(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<IKycModule>(),
            // Keyed per module (key = DbContext type name), the repo-wide outbox convention. The
            // constructor-injection spelling is [FromKeyedServices(nameof(IntegrationDbContext))];
            // a job that opens its own scope resolves the same registration by hand.
            sp.GetRequiredKeyedService<IEventPublisher>(nameof(IntegrationDbContext)),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            CancellationToken.None);

        logger.LogInformation(
            "KYC ceiling watch for tenant {TenantId}: {Examined} customer(s) examined, "
            + "{Uncapped} uncapped, {Skipped} skipped for want of limits, {Published} alert(s) "
            + "published, {AlreadyRaised} already raised this month.",
            tenantId, report.Examined, report.Uncapped, report.Skipped, report.Published,
            report.AlreadyRaised);
    }

    /// <summary>
    /// The sweep itself, with its collaborators passed in rather than resolved — so a test pins
    /// the rules without a DI container, the split every job in this repo uses.
    ///
    /// <para>
    /// <b>One customer's failure does not end the sweep.</b> Each customer is examined inside its
    /// own try, and a tenant with one unreadable KYC file still gets the alerts its other
    /// customers are owed. The ledger write is per alert, so a sweep that dies halfway has
    /// published exactly what it recorded.
    /// </para>
    /// </summary>
    internal static async Task<KycLimitWatchReport> RunAsync(
        IntegrationDbContext db,
        IKycModule kyc,
        IEventPublisher publisher,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // The dedup bucket. Derived from the run's own clock and not from the snapshot's
        // SnapshotAt: the rule is "once per month" about the ALERT, and a stale snapshot refreshed
        // last month would otherwise re-open a bucket that is already closed.
        var period = YearMonth.From(DateOnly.FromDateTime(now.UtcDateTime));

        var report = new KycLimitWatchReport();

        // IgnoreQueryFilters paired with an explicit tenant predicate, as every job in this repo
        // does: a job runs outside any HTTP request and the ambient tenant is not necessarily the
        // one being swept. The projection takes the three fields the comparison needs and leaves
        // the accounts and loans JSON in the database — the watch compares two totals and has no
        // use for a customer's account list.
        var customers = await db.CbsSnapshots
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.CrmCustomerId)
            .Select(s => new WatchedCustomer(s.CrmCustomerId, s.TotalBalance, s.MonthlyFlow))
            .ToListAsync(ct);

        foreach (var customer in customers)
        {
            report.Examined++;

            var limits = await ReadLimitsAsync(kyc, logger, tenantId, customer.CrmCustomerId, ct);

            // Null is the contract's "no KYC file at all", and a throw is a module that could not
            // answer. Both are "we do not know this customer's ceiling", and the one thing the
            // watch must not do is assume one: a default ceiling applied to a customer M02 has
            // never heard of would alert an operator about a rule nobody configured.
            if (limits is null)
            {
                report.Skipped++;
                continue;
            }

            if (!limits.IsCapped)
            {
                report.Uncapped++;
                continue;
            }

            foreach (var crossing in KycLimitEvaluator.Evaluate(
                         customer.TotalBalance, customer.MonthlyFlow, limits))
            {
                var published = await RaiseAsync(
                    db, publisher, clock, logger, tenantId, customer.CrmCustomerId, crossing,
                    limits.AlertPct, period, now, ct);

                if (published) report.Published++;
                else report.AlreadyRaised++;
            }
        }

        return report;
    }

    /// <summary>
    /// The customer's ceilings, or <c>null</c> when M02 cannot answer for it.
    ///
    /// <para>
    /// A throw is logged at warning and swallowed INTO the same null. That is deliberate and it is
    /// the conservative side: the alternative is a watch that stops at the first customer whose
    /// KYC file is in a state M02 trips over, leaving every customer after it in the tenant's list
    /// unwatched for the night — and a silently truncated sweep looks exactly like a quiet one.
    /// </para>
    /// </summary>
    private static async Task<KycLimits?> ReadLimitsAsync(
        IKycModule kyc,
        ILogger logger,
        Guid tenantId,
        Guid crmCustomerId,
        CancellationToken ct)
    {
        try
        {
            var limits = await kyc.GetLimitsAsync(tenantId, crmCustomerId, ct);

            if (limits is null)
            {
                logger.LogWarning(
                    "KYC ceiling watch skipped customer {CrmCustomerId} of tenant {TenantId}: "
                    + "M02 holds no KYC file for it, so no ceiling is known and none is assumed.",
                    crmCustomerId, tenantId);
            }

            return limits;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "KYC ceiling watch skipped customer {CrmCustomerId} of tenant {TenantId}: reading "
                + "its limits from M02 failed. No ceiling is assumed.",
                crmCustomerId, tenantId);

            return null;
        }
    }

    /// <summary>
    /// Records one crossing and publishes its event. Returns whether anything was published.
    ///
    /// <para>
    /// <b>The ledger row and the event commit together, or neither does.</b> The outbox publisher
    /// adds its row to this same <see cref="IntegrationDbContext"/> and deliberately does not save
    /// — so the single <c>SaveChangesAsync</c> below is what makes the alert and its record atomic.
    /// Published first and recorded after, a crash in between would re-alert next night; recorded
    /// first and published after, it would stay silent for a month.
    /// </para>
    ///
    /// <para>
    /// <b>The unique index is the dedup, not a read.</b> A second insert for the same customer,
    /// ceiling, severity and month loses on
    /// <c>ux_integration_kyc_limit_alert_period</c>, and the losing transaction takes its outbox
    /// row down with it — which is why nothing is published. The change tracker is cleared on that
    /// path because the two rejected rows are still Added, and left there they would be retried on
    /// the next customer's save and fail it too.
    /// </para>
    /// </summary>
    private static async Task<bool> RaiseAsync(
        IntegrationDbContext db,
        IEventPublisher publisher,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid crmCustomerId,
        KycLimitCrossing crossing,
        int alertPct,
        YearMonth period,
        DateTimeOffset detectedAt,
        CancellationToken ct)
    {
        db.KycLimitAlerts.Add(KycLimitAlert.Raise(
            tenantId, crmCustomerId, crossing.Kind, crossing.Severity, period,
            crossing.Observed, crossing.Ceiling, clock));

        // The enum travels as a string: a consumer must not have to reference this module's domain
        // assembly to read which ceiling an alert is about.
        var limitKind = crossing.Kind.ToString();

        // Published as the CONCRETE record type, never through a variable typed as the base: the
        // outbox stores typeof(TEvent).AssemblyQualifiedName, and a base-typed call would write a
        // type no consumer is subscribed to.
        if (crossing.Severity == KycLimitSeverity.Exceeded)
        {
            await publisher.PublishAsync(
                new KycLimitExceededEvent(
                    tenantId, crmCustomerId, limitKind, crossing.Observed, crossing.Ceiling,
                    detectedAt),
                ct);
        }
        else
        {
            await publisher.PublishAsync(
                new KycLimitApproachingEvent(
                    tenantId, crmCustomerId, limitKind, crossing.Observed, crossing.Ceiling,
                    alertPct, detectedAt),
                ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsAlreadyRaised())
        {
            db.ChangeTracker.Clear();

            logger.LogDebug(
                "KYC {Severity} alert on the {LimitKind} ceiling was already raised for customer "
                + "{CrmCustomerId} of tenant {TenantId} in {Period}; nothing published.",
                crossing.Severity, limitKind, crmCustomerId, tenantId, period);

            return false;
        }
    }

    /// <summary>
    /// A snapshot projected to what the comparison needs: an opaque customer reference and the two
    /// totals. Deliberately no accounts, no loans, no CBS tier.
    /// </summary>
    private sealed record WatchedCustomer(Guid CrmCustomerId, decimal TotalBalance, decimal MonthlyFlow);
}

/// <summary>What one watch did, for the log line and for the tests.</summary>
internal sealed class KycLimitWatchReport
{
    /// <summary>Snapshots read for the tenant.</summary>
    public int Examined { get; set; }

    /// <summary>Full-KYC customers: no ceiling, so nothing to compare.</summary>
    public int Uncapped { get; set; }

    /// <summary>Customers M02 could not answer for. Logged, never alerted.</summary>
    public int Skipped { get; set; }

    /// <summary>Alerts whose ledger row and event committed together.</summary>
    public int Published { get; set; }

    /// <summary>Crossings the ledger already held for this month.</summary>
    public int AlreadyRaised { get; set; }
}
