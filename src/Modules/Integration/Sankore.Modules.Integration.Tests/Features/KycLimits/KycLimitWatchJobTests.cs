namespace Sankore.Modules.Integration.Tests.Features.KycLimits;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.KycLimits;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

/// <summary>
/// INT-22 end to end for one tenant: the comparison, the two events, and the ledger that makes
/// each of them a once-per-month statement.
///
/// <para>
/// Every run goes through the REAL outbox publisher and asserts on what reached
/// <c>outbox_messages</c>, not on a recording double — criterion 2's "once" is a property of what
/// was committed, and a double would report an event a failed commit never published.
/// </para>
/// </summary>
public sealed class KycLimitWatchJobTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Customer = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid OtherCustomer = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    [Fact]
    public async Task A_capped_customer_at_the_balance_threshold_is_alerted_once()
    {
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        var report = await harness.RunAsync(KycLimitsTestContext.March);

        report.Published.Should().Be(1);

        await using var db = harness.Db();

        var approaching = KycLimitsTestContext.Published<KycLimitApproachingEvent>(db);

        approaching.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new KycLimitApproachingEvent(
                Tenant, Customer, nameof(KycLimitKind.Balance), 200_000m, 250_000m, 80,
                KycLimitsTestContext.March),
                options => options.Excluding(e => e.EventId).Excluding(e => e.OccurredAt));

        KycLimitsTestContext.Published<KycLimitExceededEvent>(db).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_run_twice_publishes_once_because_the_ledger_holds()
    {
        // The dedup is the unique index, not a read: a retry after a partial failure would pass a
        // read-then-write check twice and hand the operator a second upgrade task.
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        var first = await harness.RunAsync(KycLimitsTestContext.March);
        var second = await harness.RunAsync(KycLimitsTestContext.March);

        first.Published.Should().Be(1);
        second.Published.Should().Be(0);
        second.AlreadyRaised.Should().Be(1);

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitApproachingEvent>(db).Should().HaveCount(1);
        (await db.KycLimitAlerts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_next_month_is_a_new_bucket_and_publishes_again()
    {
        // "Once per customer and per month": the alert is repeated the following month because a
        // customer still at the ceiling in April is news again, and the period column is what
        // separates the two.
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        await harness.RunAsync(KycLimitsTestContext.March);
        var april = await harness.RunAsync(KycLimitsTestContext.April);

        april.Published.Should().Be(1);

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitApproachingEvent>(db).Should().HaveCount(2);

        var periods = await db.KycLimitAlerts.Select(a => a.Period).ToListAsync();
        periods.Should().BeEquivalentTo(["2026-03", "2026-04"]);
    }

    [Fact]
    public async Task A_customer_over_the_ceiling_gets_exceeded_and_not_approaching()
    {
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 300_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        await harness.RunAsync(KycLimitsTestContext.March);

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitExceededEvent>(db).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new KycLimitExceededEvent(
                Tenant, Customer, nameof(KycLimitKind.Balance), 300_000m, 250_000m,
                KycLimitsTestContext.March),
                options => options.Excluding(e => e.EventId).Excluding(e => e.OccurredAt));

        KycLimitsTestContext.Published<KycLimitApproachingEvent>(db).Should().BeEmpty();

        var ledger = await db.KycLimitAlerts.SingleAsync();
        ledger.Severity.Should().Be(KycLimitSeverity.Exceeded);
    }

    [Fact]
    public async Task An_exceeded_alert_is_deduped_per_severity_like_an_approaching_one()
    {
        // A reading of criterion 3, not something it states: the criterion puts the once-a-month
        // rule only on Approaching, but a customer who stays over the ceiling would otherwise
        // produce one Exceeded every night for as long as they stay over. The ledger's unique
        // index already carries Severity for exactly this.
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 300_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        await harness.RunAsync(KycLimitsTestContext.March);
        await harness.RunAsync(KycLimitsTestContext.March.AddDays(1));

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitExceededEvent>(db).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_customer_crossing_approaching_then_the_ceiling_gets_both_statements()
    {
        // The two severities are separate ledger rows on purpose. A customer warned at 80 % in
        // March and over the ceiling a week later must still produce the breach — it is a
        // different fact, and M02 opens a different task for it.
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        await harness.RunAsync(KycLimitsTestContext.March);

        await harness.RefreshAsync(Customer, totalBalance: 300_000m, monthlyFlow: 0m);

        await harness.RunAsync(KycLimitsTestContext.March.AddDays(7));

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitApproachingEvent>(db).Should().HaveCount(1);
        KycLimitsTestContext.Published<KycLimitExceededEvent>(db).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_full_kyc_customer_is_never_alerted()
    {
        // No ceiling applies to a full KYC file. This is the customer who must never receive an
        // upgrade task, however large the figures.
        var harness = new Harness();
        await harness.SeedAsync(
            KycLimitsTestContext.Snapshot(Tenant, Customer, 50_000_000m, 90_000_000m));
        harness.Limits(Customer, KycLimitsTestContext.Uncapped());

        var report = await harness.RunAsync(KycLimitsTestContext.March);

        report.Uncapped.Should().Be(1);
        report.Published.Should().Be(0);

        await using var db = harness.Db();

        db.OutboxMessages.Should().BeEmpty();
        (await db.KycLimitAlerts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_flow_ceiling_is_watched_independently_of_the_balance_ceiling()
    {
        var harness = new Harness();

        // Balance at 0.4 % of its ceiling, flow at 80 % of its own.
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 1_000m, 400_000m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        await harness.RunAsync(KycLimitsTestContext.March);

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitApproachingEvent>(db).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                LimitKind = nameof(KycLimitKind.Flow),
                Observed = 400_000m,
                Ceiling = 500_000m,
                ThresholdPercent = 80,
            });

        (await db.KycLimitAlerts.SingleAsync()).LimitKind.Should().Be(KycLimitKind.Flow);
    }

    [Fact]
    public async Task Both_ceilings_of_one_customer_produce_one_alert_each()
    {
        var harness = new Harness();
        await harness.SeedAsync(
            KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 400_000m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        var report = await harness.RunAsync(KycLimitsTestContext.March);

        report.Published.Should().Be(2);

        await using var db = harness.Db();

        var kinds = await db.KycLimitAlerts.Select(a => a.LimitKind).ToListAsync();
        kinds.Should().BeEquivalentTo([KycLimitKind.Balance, KycLimitKind.Flow]);
    }

    [Fact]
    public async Task The_ceilings_and_the_threshold_are_whatever_the_kyc_module_answers()
    {
        // The same figures, two tenants' configurations, opposite verdicts — and no amount or
        // percentage anywhere in this module to disagree with either.
        var strict = new Harness();
        await strict.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 60_000m, 0m));
        strict.Limits(Customer, KycLimitsTestContext.Capped(maxBalance: 100_000m, alertPct: 50));

        var lax = new Harness();
        await lax.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 60_000m, 0m));
        lax.Limits(Customer, KycLimitsTestContext.Capped(maxBalance: 1_000_000m, alertPct: 95));

        var strictReport = await strict.RunAsync(KycLimitsTestContext.March);
        var laxReport = await lax.RunAsync(KycLimitsTestContext.March);

        strictReport.Published.Should().Be(1);
        laxReport.Published.Should().Be(0);

        await using var db = strict.Db();

        KycLimitsTestContext.Published<KycLimitApproachingEvent>(db).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Ceiling = 100_000m, ThresholdPercent = 50 });
    }

    [Fact]
    public async Task A_customer_the_kyc_module_cannot_answer_for_is_skipped_and_logged()
    {
        // Null is the contract's "no KYC file at all". The one thing the watch must not do is
        // assume a ceiling for a customer M02 has never heard of.
        var harness = new Harness();
        await harness.SeedAsync(
            KycLimitsTestContext.Snapshot(Tenant, Customer, 10_000_000m, 0m));
        harness.Limits(Customer, limits: null);

        var logger = new RecordingLogger();
        var report = await harness.RunAsync(KycLimitsTestContext.March, logger);

        report.Skipped.Should().Be(1);
        report.Published.Should().Be(0);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(Customer.ToString());

        await using var db = harness.Db();
        db.OutboxMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_customer_whose_limits_throw_is_skipped_and_the_sweep_carries_on()
    {
        // A truncated sweep looks exactly like a quiet one, so one unreadable KYC file must not
        // cost every customer after it in the list its night's watch.
        var harness = new Harness();
        await harness.SeedAsync(
            KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 0m),
            KycLimitsTestContext.Snapshot(Tenant, OtherCustomer, 300_000m, 0m));

        harness.Kyc.GetLimitsAsync(Tenant, Customer, Arg.Any<CancellationToken>())
            .Returns<KycLimits?>(_ => throw new InvalidOperationException("KYC unavailable"));
        harness.Limits(OtherCustomer, KycLimitsTestContext.Capped());

        var logger = new RecordingLogger();
        var report = await harness.RunAsync(KycLimitsTestContext.March, logger);

        report.Skipped.Should().Be(1);
        report.Published.Should().Be(1);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitExceededEvent>(db).Should().ContainSingle()
            .Which.CrmCustomerId.Should().Be(OtherCustomer);
    }

    [Fact]
    public async Task The_ledger_row_and_the_event_commit_together()
    {
        // The outbox publisher adds its row to the watch's own DbContext and does not save, so one
        // SaveChangesAsync carries both. Published-then-recorded would re-alert after a crash in
        // between; recorded-then-published would stay silent for a month.
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        await harness.RunAsync(KycLimitsTestContext.March);

        await using (var db = harness.Db())
        {
            (await db.KycLimitAlerts.CountAsync()).Should().Be(1);
            db.OutboxMessages.Should().HaveCount(1);
        }

        // And the losing side of the unique index takes its outbox row down with it: the second
        // run adds both rows, the index rejects the commit, and NEITHER lands.
        await harness.RunAsync(KycLimitsTestContext.March);

        await using (var db = harness.Db())
        {
            (await db.KycLimitAlerts.CountAsync()).Should().Be(1);
            db.OutboxMessages.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task A_rejected_alert_does_not_poison_the_customers_examined_after_it()
    {
        // The rejected ledger row and its outbox row are still Added on the context. Left there
        // they would be retried on the next customer's SaveChanges and fail that one too — which
        // is why the catch clears the change tracker.
        var harness = new Harness();
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, Customer, 200_000m, 0m));
        harness.Limits(Customer, KycLimitsTestContext.Capped());

        await harness.RunAsync(KycLimitsTestContext.March);

        // The second customer appears only for the second run, so it is examined after the one
        // whose alert the ledger now rejects.
        await harness.SeedAsync(KycLimitsTestContext.Snapshot(Tenant, OtherCustomer, 400_000m, 0m));
        harness.Limits(OtherCustomer, KycLimitsTestContext.Capped());

        var second = await harness.RunAsync(KycLimitsTestContext.March);

        second.AlreadyRaised.Should().Be(1);
        second.Published.Should().Be(1);

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitExceededEvent>(db).Should().ContainSingle()
            .Which.CrmCustomerId.Should().Be(OtherCustomer);
    }

    [Fact]
    public async Task Another_tenants_snapshots_are_never_examined()
    {
        // IgnoreQueryFilters paired with an explicit tenant predicate: a job runs outside any HTTP
        // request, so the ambient tenant is not necessarily the one being swept.
        var otherTenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        var harness = new Harness();
        await harness.SeedAsync(
            KycLimitsTestContext.Snapshot(Tenant, Customer, 300_000m, 0m),
            KycLimitsTestContext.Snapshot(otherTenant, OtherCustomer, 300_000m, 0m));

        harness.Limits(Customer, KycLimitsTestContext.Capped());
        harness.Limits(OtherCustomer, KycLimitsTestContext.Capped());

        var report = await harness.RunAsync(KycLimitsTestContext.March);

        report.Examined.Should().Be(1);

        await using var db = harness.Db();

        KycLimitsTestContext.Published<KycLimitExceededEvent>(db).Should().ContainSingle()
            .Which.CrmCustomerId.Should().Be(Customer);
    }

    /// <summary>
    /// One logical database, one emulated unique index, one <see cref="IKycModule"/> substitute —
    /// shared by every context a test opens, the way a real store and a real module are.
    /// </summary>
    private sealed class Harness
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();
        private readonly LedgerUniqueIndexInterceptor _ledgerIndex = new();

        internal IKycModule Kyc { get; } = Substitute.For<IKycModule>();

        internal IntegrationDbContext Db() =>
            KycLimitsTestContext.NewDb(Tenant, _databaseName, _ledgerIndex);

        internal void Limits(Guid customerId, KycLimits? limits)
            => Kyc.GetLimitsAsync(Arg.Any<Guid>(), customerId, Arg.Any<CancellationToken>())
                  .Returns(limits);

        internal async Task SeedAsync(params CbsSnapshot[] snapshots)
        {
            await using var db = Db();

            db.CbsSnapshots.AddRange(snapshots);
            await db.SaveChangesAsync();
        }

        /// <summary>Moves a customer's figures, the way INT-21's sync would overnight.</summary>
        internal async Task RefreshAsync(Guid customerId, decimal totalBalance, decimal monthlyFlow)
        {
            await using var db = Db();

            var snapshot = await db.CbsSnapshots
                .AsTracking()
                .SingleAsync(s => s.CrmCustomerId == customerId);

            snapshot.Update(
                "[]", "[]", totalBalance, monthlyFlow, kycLevelInCbs: null,
                new KycLimitsTestContext.FixedClock(KycLimitsTestContext.March));

            await db.SaveChangesAsync();
        }

        internal async Task<KycLimitWatchReport> RunAsync(
            DateTimeOffset now, ILogger? logger = null)
        {
            await using var db = Db();

            return await KycLimitWatchJob.RunAsync(
                db,
                Kyc,
                KycLimitsTestContext.Publisher(db),
                new KycLimitsTestContext.FixedClock(now),
                logger ?? NullLogger.Instance,
                Tenant,
                CancellationToken.None);
        }
    }
}
