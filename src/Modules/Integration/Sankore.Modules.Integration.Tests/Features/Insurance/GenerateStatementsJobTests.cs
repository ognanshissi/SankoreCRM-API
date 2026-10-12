namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Statements;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;
using static Sankore.Modules.Integration.Tests.Features.Insurance.InsuranceTestHarness;

/// <summary>
/// ASS-10: one month's bordereau per insurer, and the commission it earns.
///
/// <para>
/// The two cases that drove the design are
/// <see cref="The_header_s_commission_is_the_arithmetic_sum_of_the_printed_lines"/> — the first
/// thing an insurer's back office checks, and the reason a contre-passation carries a NEGATIVE
/// amount — and <see cref="A_finalised_month_is_NOT_rebuilt"/>, without which a nightly
/// regeneration would silently restate figures the institution had already signed.
/// </para>
/// </summary>
public sealed class GenerateStatementsJobTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();

    /// <summary>The 1st of June, which is when the job runs for May.</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 5, 0, 0, TimeSpan.Zero);

    private const string Period = "2026-05";

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private Guid _connectionId;
    private Guid _productId;

    private async Task<PolicyRecord> SeedPolicyAsync(
        DateOnly? effective = null,
        PolicyStatus status = PolicyStatus.Issued,
        DateTimeOffset? statusChangedAt = null,
        decimal premium = 7_500m,
        string currency = "XOF",
        decimal commissionRate = 0.15m,
        bool withProduct = true)
    {
        await using var db = _factory.CreateContext();

        if (_connectionId == Guid.Empty)
        {
            var connection = SeedConnection(db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");
            _connectionId = connection.Id;

            _productId = SeedProduct(
                db, Tenant, connection.Id, commissionRate: commissionRate).Id;
        }

        var policy = PolicyRecord.Create(
            tenantId: Tenant,
            connectionId: _connectionId,
            crmCustomerId: Customer,
            externalPolicyId: $"POL-{Guid.NewGuid():N}"[..20],
            policyNumber: "POL-CI-0001",
            insurerProductCode: "ASS-VIE-EMP",
            status: PolicyStatus.Issued,
            effectiveDate: effective ?? new DateOnly(2026, 5, 10),
            premiumAmount: premium,
            currency: currency,
            periodicity: PremiumPeriodicity.Monthly,
            clock: Clock(statusChangedAt ?? Now),
            productId: withProduct ? _productId : null);

        if (status != PolicyStatus.Issued)
        {
            policy.ApplySync(
                status: status,
                policyNumber: "POL-CI-0001",
                effectiveDate: effective ?? new DateOnly(2026, 5, 10),
                expiryDate: null,
                nextDueDate: null,
                premiumAmount: premium,
                periodicity: PremiumPeriodicity.Monthly,
                clock: Clock(statusChangedAt ?? Now));
        }

        db.Policies.Add(policy);
        await db.SaveChangesAsync();

        return policy;
    }

    private async Task SeedPaidInstalmentAsync(
        Guid policyId, DateTimeOffset paidAt, decimal amount = 7_500m, string currency = "XOF")
    {
        await using var db = _factory.CreateContext();

        var instalment = PremiumInstalment.Create(
            tenantId: Tenant,
            policyId: policyId,
            sequenceNo: 1,
            dueDate: DateOnly.FromDateTime(paidAt.UtcDateTime),
            amount: amount,
            currency: currency,
            clock: Clock(paidAt));

        instalment.BeginDebit(Guid.NewGuid(), Clock(paidAt));
        instalment.MarkPaid("CBS-DEB-0001", Clock(paidAt));

        db.PremiumInstalments.Add(instalment);
        await db.SaveChangesAsync();
    }

    private async Task<int> RunAsync(string period = Period)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock(Now));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped(_ => _factory.CreateContext());

        await using var provider = services.BuildServiceProvider();

        var job = new GenerateStatementsJob(
            new StatementScopeFactory(provider), NullLogger<GenerateStatementsJob>.Instance);

        // Through RunAsync when the period under test IS the previous month, so the cron path is
        // the one exercised; otherwise through the shared method the endpoint would use.
        if (period == Period)
        {
            await job.RunAsync(Tenant, CancellationToken.None);
            return 1;
        }

        await using var db = _factory.CreateContext();
        return await job.GenerateAsync(db, Clock(Now), Tenant, period, CancellationToken.None);
    }

    private async Task<InsurerStatement?> StatementAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.InsurerStatements
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Period == Period);
    }

    private async Task<List<InsurerStatementLine>> LinesAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.InsurerStatementLines
            .IgnoreQueryFilters()
            .OrderBy(l => l.OccurredOn)
            .ToListAsync();
    }

    // ── the period ──────────────────────────────────────────────────────────

    [Fact]
    public void The_job_builds_the_month_BEFORE_the_one_it_runs_in()
    {
        // A statement for the running month would be a document whose figures change every
        // night, which is not something to justify to an insurer.
        StatementPeriod.PreviousOf(new DateOnly(2026, 6, 1)).Should().Be("2026-05");
        StatementPeriod.PreviousOf(new DateOnly(2026, 1, 15)).Should().Be("2025-12");
    }

    [Theory]
    [InlineData("2026-05", "2026-05-01", "2026-05-31")]
    [InlineData("2026-02", "2026-02-01", "2026-02-28")]
    [InlineData("2024-02", "2024-02-01", "2024-02-29")]
    public void A_period_s_bounds_are_inclusive_and_know_February(
        string period, string first, string last)
    {
        var bounds = StatementPeriod.Bounds(period);

        bounds.Should().NotBeNull();
        // InvariantCulture on both sides, which is the point of the case: the analyser is right
        // that a locale-dependent parse here would make the assertion depend on the host.
        bounds!.Value.First.Should().Be(DateOnly.Parse(first, CultureInfo.InvariantCulture));
        bounds.Value.Last.Should().Be(DateOnly.Parse(last, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-13")]
    [InlineData("mai 2026")]
    [InlineData("2026-05-01")]
    public void Anything_that_is_not_a_month_is_refused(string period)
        => StatementPeriod.IsValid(period).Should().BeFalse();

    // ── the lines ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_policy_taking_effect_in_the_period_is_an_adhesion()
    {
        await SeedPolicyAsync(effective: new DateOnly(2026, 5, 10));

        await RunAsync();

        var line = (await LinesAsync()).Should().ContainSingle().Subject;
        line.LineType.Should().Be(StatementLineType.Subscription);
        line.OccurredOn.Should().Be(new DateOnly(2026, 5, 10));

        // A census line: the premium it carries is the contract's, and the rate is ZERO so the
        // adhésion does not earn a commission the collected premium will earn again.
        line.Amount.Should().Be(7_500m);
        line.CommissionRate.Should().Be(0m);
        line.CommissionAmount.Should().Be(0m);
    }

    [Fact]
    public async Task A_policy_taking_effect_OUTSIDE_the_period_is_not_on_it()
    {
        await SeedPolicyAsync(effective: new DateOnly(2026, 4, 30));

        await RunAsync();

        (await LinesAsync()).Should().BeEmpty();
        (await StatementAsync()).Should().BeNull("a month with no movement needs no document");
    }

    [Fact]
    public async Task A_collected_premium_is_where_the_commission_is_earned()
    {
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 4, 1));
        await SeedPaidInstalmentAsync(policy.Id, new DateTimeOffset(2026, 5, 15, 3, 45, 0, TimeSpan.Zero));

        await RunAsync();

        var line = (await LinesAsync()).Should().ContainSingle().Subject;
        line.LineType.Should().Be(StatementLineType.PremiumPaid);
        line.Amount.Should().Be(7_500m);
        line.CommissionRate.Should().Be(0.15m);
        line.CommissionAmount.Should().Be(1_125m);

        // Snapshotted, not joined: the CBS reference is on the line.
        line.CbsReference.Should().Be("CBS-DEB-0001");
        line.PolicyNumber.Should().Be("POL-CI-0001");
        line.InstalmentId.Should().NotBeNull();
    }

    [Fact]
    public async Task An_instalment_still_in_flight_is_NOT_a_collection()
    {
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 4, 1));

        await using (var db = _factory.CreateContext())
        {
            var instalment = PremiumInstalment.Create(
                tenantId: Tenant, policyId: policy.Id, sequenceNo: 1,
                dueDate: new DateOnly(2026, 5, 15), amount: 7_500m, currency: "XOF",
                clock: Clock(Now));

            instalment.BeginDebit(Guid.NewGuid(), Clock(Now));

            db.PremiumInstalments.Add(instalment);
            await db.SaveChangesAsync();
        }

        await RunAsync();

        (await LinesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_policy_cancelled_in_the_period_is_a_resiliation_with_no_amount()
    {
        // A cancelled contract gives nothing back by itself — the money that goes back is a
        // contre-passation, which has its own line.
        await SeedPolicyAsync(
            effective: new DateOnly(2026, 1, 1),
            status: PolicyStatus.Cancelled,
            statusChangedAt: new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero));

        await RunAsync();

        var line = (await LinesAsync()).Should().ContainSingle().Subject;
        line.LineType.Should().Be(StatementLineType.Cancellation);
        line.OccurredOn.Should().Be(new DateOnly(2026, 5, 20));
        line.Amount.Should().Be(0m);
        line.CbsReference.Should().BeNull();
    }

    [Fact]
    public async Task A_policy_with_no_catalogue_entry_earns_ZERO_rather_than_an_invented_rate()
    {
        // Synchronised from the insurer with no product mapping. Inventing a rate would bill an
        // insurer a commission nobody configured.
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 4, 1), withProduct: false);
        await SeedPaidInstalmentAsync(policy.Id, new DateTimeOffset(2026, 5, 15, 3, 45, 0, TimeSpan.Zero));

        await RunAsync();

        var line = (await LinesAsync()).Single(l => l.LineType == StatementLineType.PremiumPaid);
        line.CommissionRate.Should().Be(0m);
        line.CommissionAmount.Should().Be(0m);
    }

    // ── the header ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_header_s_commission_is_the_arithmetic_sum_of_the_printed_lines()
    {
        // The first thing an insurer's back office checks. Two collections in the month, plus an
        // adhésion that earns nothing.
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 5, 3));
        await SeedPaidInstalmentAsync(policy.Id, new DateTimeOffset(2026, 5, 5, 3, 45, 0, TimeSpan.Zero));

        await using (var db = _factory.CreateContext())
        {
            var second = PremiumInstalment.Create(
                tenantId: Tenant, policyId: policy.Id, sequenceNo: 2,
                dueDate: new DateOnly(2026, 5, 25), amount: 7_500m, currency: "XOF",
                clock: Clock(Now));

            second.BeginDebit(Guid.NewGuid(), Clock(Now));
            second.MarkPaid("CBS-DEB-0002",
                Clock(new DateTimeOffset(2026, 5, 25, 3, 45, 0, TimeSpan.Zero)));

            db.PremiumInstalments.Add(second);
            await db.SaveChangesAsync();
        }

        await RunAsync();

        var statement = (await StatementAsync())!;
        var lines = await LinesAsync();

        statement.SubscriptionCount.Should().Be(1);
        statement.PremiumCollected.Should().Be(15_000m);
        statement.CommissionAmount.Should().Be(2_250m);
        statement.Currency.Should().Be("XOF");

        // The property itself, asserted as arithmetic rather than as two constants.
        statement.CommissionAmount.Should().Be(lines.Sum(l => l.CommissionAmount));
    }

    [Fact]
    public async Task A_month_with_no_movement_produces_no_document_at_all()
    {
        await SeedPolicyAsync(effective: new DateOnly(2026, 2, 1));

        await RunAsync();

        (await StatementAsync()).Should().BeNull();
    }

    // ── regeneration ────────────────────────────────────────────────────────

    [Fact]
    public async Task Running_twice_rebuilds_the_draft_rather_than_duplicating_its_lines()
    {
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 5, 10));
        await SeedPaidInstalmentAsync(policy.Id, new DateTimeOffset(2026, 5, 15, 3, 45, 0, TimeSpan.Zero));

        await RunAsync();
        var afterFirst = (await LinesAsync()).Count;

        await RunAsync();

        (await LinesAsync()).Should().HaveCount(afterFirst);

        await using var db = _factory.CreateContext();
        (await db.InsurerStatements.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_late_movement_appears_on_the_next_rebuild()
    {
        // What a rebuildable draft is FOR: a premium whose debit landed after the 1st still
        // belongs to May's bordereau.
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 5, 10));

        await RunAsync();
        (await LinesAsync()).Should().HaveCount(1);

        await SeedPaidInstalmentAsync(policy.Id, new DateTimeOffset(2026, 5, 31, 3, 45, 0, TimeSpan.Zero));

        await RunAsync();

        (await LinesAsync()).Should().HaveCount(2);
        (await StatementAsync())!.PremiumCollected.Should().Be(7_500m);
    }

    [Fact]
    public async Task A_finalised_month_is_NOT_rebuilt()
    {
        // Without this a nightly regeneration would silently restate figures the institution had
        // already justified to its insurer.
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 5, 10));
        await RunAsync();

        await using (var db = _factory.CreateContext())
        {
            var statement = await db.InsurerStatements
                .IgnoreQueryFilters().AsTracking().FirstAsync();

            statement.Finalise(Actor, Clock(Now));
            await db.SaveChangesAsync();
        }

        await SeedPaidInstalmentAsync(policy.Id, new DateTimeOffset(2026, 5, 31, 3, 45, 0, TimeSpan.Zero));

        var act = async () => await RunAsync();

        await act.Should().NotThrowAsync("the other insurers of the tenant must still be built");

        var after = (await StatementAsync())!;
        after.Status.Should().Be(StatementStatus.Finalised);
        after.PremiumCollected.Should().Be(0m, "the signed figures are untouched");
        (await LinesAsync()).Should().HaveCount(1);
    }

    // ── currency ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_movement_in_another_currency_is_left_OUT_rather_than_summed_in()
    {
        // One statement per insurer per month, with no currency dimension in
        // ux_ins_statement_period. A bordereau mixing units is the one arithmetic error worse
        // than a missing line.
        await SeedPolicyAsync(effective: new DateOnly(2026, 5, 10), currency: "XOF");
        await SeedPolicyAsync(effective: new DateOnly(2026, 5, 11), currency: "XOF");
        await SeedPolicyAsync(effective: new DateOnly(2026, 5, 12), currency: "EUR");

        await RunAsync();

        var statement = (await StatementAsync())!;
        statement.Currency.Should().Be("XOF");
        statement.SubscriptionCount.Should().Be(2);

        (await LinesAsync()).Should().AllSatisfy(l => l.Currency.Should().Be("XOF"));
    }

    // ── the CSV ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_CSV_renders_the_detail_and_a_TOTAL_line_with_an_invariant_decimal()
    {
        var policy = await SeedPolicyAsync(effective: new DateOnly(2026, 5, 3));
        await SeedPaidInstalmentAsync(policy.Id, new DateTimeOffset(2026, 5, 5, 3, 45, 0, TimeSpan.Zero));

        await RunAsync();

        var statement = (await StatementAsync())!;
        var lines = await LinesAsync();

        var csv = System.Text.Encoding.UTF8.GetString(StatementCsv.Render(
            new StatementDetailDto(
                StatementDto.From(statement),
                [.. lines.Select(StatementLineDto.From)])));

        // A dot and not a comma: the separator IS the comma's job here, so a decimal rendered
        // with a French process culture would silently add a column.
        csv.Should().Contain("7500.00");
        csv.Should().Contain("1125.00");
        csv.Should().Contain("0.1500");
        csv.Should().Contain("TOTAL");
        csv.Should().Contain("adhesions=1");

        // No identity travels — only the opaque CRM id the two systems reconcile on.
        csv.Should().Contain(Customer.ToString("D"));
    }

    [Fact]
    public void A_field_that_would_break_a_row_is_quoted_and_a_formula_is_neutralised()
    {
        var detail = new StatementDetailDto(
            new StatementDto(
                Guid.NewGuid(), Guid.NewGuid(), Period, nameof(StatementStatus.Draft),
                0, 0, 0, 0m, 0m, 0m, "XOF", Now, null, null, null, null, true),
            [
                new StatementLineDto(
                    Guid.NewGuid(), nameof(StatementLineType.PremiumPaid), Guid.NewGuid(),
                    null, null, Customer, new DateOnly(2026, 5, 5), 1m, "XOF", 0m, 0m,
                    // A policy number out of an external system: not assumed to be free of the
                    // separator.
                    PolicyNumber: "POL;CI;0001",
                    // And one that a spreadsheet would evaluate.
                    InsurerProductCode: "=1+1",
                    CbsReference: null),
            ]);

        var csv = System.Text.Encoding.UTF8.GetString(StatementCsv.Render(detail));

        csv.Should().Contain("\"POL;CI;0001\"");
        csv.Should().Contain("'=1+1");
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private sealed class StatementScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
