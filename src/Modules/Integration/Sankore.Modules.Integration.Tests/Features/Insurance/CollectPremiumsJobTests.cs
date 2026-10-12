namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.Premiums;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// ASS-08 end to end: the schedule, the debits, and what a refusal costs.
///
/// <para>
/// The idempotence of the generator is the case that matters most, and it is also the one the
/// test suite can only half prove: <c>ux_ins_premium_instalment_due</c> is what makes it true
/// under concurrency, and EF InMemory honours no unique index. So these cases assert on ROW
/// COUNTS, which is the device this repository already uses where an index cannot be exercised.
/// </para>
/// </summary>
public sealed class CollectPremiumsJobTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 3, 45, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 6, 1);

    private readonly MovablePremiumClock _clock = new(Now);
    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly ICoreBankingGateway _cbs = Substitute.For<ICoreBankingGateway>();
    private readonly List<InstalmentDebitRequest> _debits = [];

    private Guid _commandId = Guid.NewGuid();

    public CollectPremiumsJobTests()
    {
        _integration.CoreBanking.Returns(_cbs);

        _cbs.RequestInstalmentDebitAsync(
                Arg.Do<InstalmentDebitRequest>(_debits.Add), Arg.Any<CancellationToken>())
            .Returns(_ => new IntegrationCommandId(_commandId));
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<PolicyRecord> SeedPolicyAsync(
        PolicyStatus status = PolicyStatus.Issued,
        PremiumPeriodicity periodicity = PremiumPeriodicity.Monthly,
        DateOnly? effective = null,
        DateOnly? expiry = null,
        bool withAccount = true,
        bool withProduct = true,
        int retryLimit = 3,
        int retryIntervalDays = 5)
    {
        await using var db = _factory.CreateContext();

        var connection = InsuranceTestHarness.SeedConnection(
            db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        var product = InsuranceTestHarness.SeedProduct(db, Tenant, connection.Id);
        await db.SaveChangesAsync();

        if (withProduct)
        {
            // The retry policy is read from the product at decision time, so it is set here
            // rather than copied onto the instalment.
            await using var update = _factory.CreateContext();
            var tracked = await update.InsuranceProducts.AsTracking()
                .IgnoreQueryFilters().FirstAsync(p => p.Id == product.Id);

            typeof(InsuranceProduct).GetProperty(nameof(InsuranceProduct.PremiumRetryLimit))!
                .SetValue(tracked, retryLimit);
            typeof(InsuranceProduct)
                .GetProperty(nameof(InsuranceProduct.PremiumRetryIntervalDays))!
                .SetValue(tracked, retryIntervalDays);

            await update.SaveChangesAsync();
        }

        var policy = PolicyRecord.Create(
            tenantId: Tenant,
            connectionId: connection.Id,
            crmCustomerId: Customer,
            externalPolicyId: "POL-2026-0001",
            policyNumber: "POL-2026-0001",
            insurerProductCode: "ASS-VIE-EMP",
            status: status,
            effectiveDate: effective ?? Today.AddMonths(-2),
            premiumAmount: 7_500m,
            currency: "XOF",
            periodicity: periodicity,
            clock: _clock,
            productId: withProduct ? product.Id : null,
            expiryDate: expiry);

        await using var save = _factory.CreateContext();
        save.Policies.Add(policy);

        if (withAccount)
        {
            save.References.Add(IntegrationReference.Create(
                tenantId: Tenant,
                connectionId: connection.Id,
                kind: IntegrationKind.Fake,
                entityType: IntegrationEntityTypes.Account,
                crmId: Customer,
                externalId: "ACC-CI-0001",
                clock: InsuranceTestHarness.Clock()));
        }

        await save.SaveChangesAsync();
        return policy;
    }

    private async Task RunAsync()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_integration);
        services.AddScoped(_ => _factory.CreateContext());

        await using var provider = services.BuildServiceProvider();

        await new CollectPremiumsJob(
                new FixedPremiumScopeFactory(provider), NullLogger<CollectPremiumsJob>.Instance)
            .RunAsync(Tenant, CancellationToken.None);
    }

    private async Task<List<PremiumInstalment>> InstalmentsAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.PremiumInstalments.IgnoreQueryFilters()
            .OrderBy(i => i.DueDate).ToListAsync();
    }

    // ── the schedule ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_live_policy_gets_its_schedule_materialised()
    {
        await SeedPolicyAsync(effective: Today.AddMonths(-2));

        await RunAsync();

        var instalments = await InstalmentsAsync();

        // Two months back plus a year ahead, monthly.
        instalments.Should().HaveCountGreaterThan(12);
        instalments.Should().AllSatisfy(i => i.Amount.Should().Be(7_500m));
        instalments.Select(i => i.SequenceNo).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Running_the_job_twice_schedules_nothing_further()
    {
        // The property the unique index depends on, asserted on row COUNTS because EF InMemory
        // honours no unique index.
        await SeedPolicyAsync();

        await RunAsync();
        var afterFirst = (await InstalmentsAsync()).Count;

        await RunAsync();

        (await InstalmentsAsync()).Should().HaveCount(afterFirst);
    }

    [Theory]
    [InlineData(PolicyStatus.Cancelled)]
    [InlineData(PolicyStatus.Expired)]
    public async Task A_dead_policy_gets_no_schedule(PolicyStatus status)
    {
        // Scheduling for one would debit a premium for cover the customer no longer has.
        await SeedPolicyAsync(status: status);

        await RunAsync();

        (await InstalmentsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_suspended_policy_IS_scheduled()
    {
        // Suspended is live: a policy suspended for non-payment still owes the premium that
        // would lift the suspension.
        await SeedPolicyAsync(status: PolicyStatus.Suspended);

        await RunAsync();

        (await InstalmentsAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_schedule_stops_at_the_expiry()
    {
        await SeedPolicyAsync(
            effective: Today.AddMonths(-1), expiry: Today.AddMonths(1));

        await RunAsync();

        (await InstalmentsAsync()).Should()
            .AllSatisfy(i => i.DueDate.Should().BeOnOrBefore(Today.AddMonths(1)));
    }

    // ── the debits ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_instalment_that_has_fallen_due_is_debited()
    {
        await SeedPolicyAsync(effective: Today.AddMonths(-2));

        await RunAsync();

        // Three due: two months back and today.
        _debits.Should().HaveCount(3);
        _debits.Should().AllSatisfy(d =>
        {
            d.Amount.Should().Be(7_500m);
            d.Attempt.Should().Be(1);
        });

        var debiting = (await InstalmentsAsync())
            .Where(i => i.Status == InstalmentStatus.Debiting).ToList();

        debiting.Should().HaveCount(3);
        debiting.Should().AllSatisfy(i => i.DebitCommandId.Should().NotBeNull());
    }

    [Fact]
    public async Task A_future_instalment_is_not_debited()
    {
        await SeedPolicyAsync(effective: Today.AddMonths(1));

        await RunAsync();

        _debits.Should().BeEmpty();
        (await InstalmentsAsync()).Should()
            .AllSatisfy(i => i.Status.Should().Be(InstalmentStatus.Scheduled));
    }

    [Fact]
    public async Task A_customer_with_no_account_reference_is_left_SCHEDULED_not_failed()
    {
        // Nothing was attempted, so nothing failed: tomorrow's run tries again once the
        // reference exists. Marking it Failed would spend a retry on a configuration gap.
        await SeedPolicyAsync(withAccount: false);

        await RunAsync();

        _debits.Should().BeEmpty();
        (await InstalmentsAsync()).Should()
            .AllSatisfy(i => i.Status.Should().Be(InstalmentStatus.Scheduled));
    }

    [Fact]
    public async Task A_core_banking_outage_leaves_the_instalment_untouched()
    {
        await SeedPolicyAsync();

        _cbs.RequestInstalmentDebitAsync(
                Arg.Any<InstalmentDebitRequest>(), Arg.Any<CancellationToken>())
            .Returns<IntegrationCommandId>(_ => throw new DomainException(
                $"{IntegrationErrors.NoActiveConnection}: no core banking connection."));

        var act = async () => await RunAsync();

        await act.Should().NotThrowAsync();

        (await InstalmentsAsync()).Should()
            .AllSatisfy(i => i.Status.Should().Be(InstalmentStatus.Scheduled));
    }

    // ── the outcomes ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_debit_marks_the_instalment_paid()
    {
        await SeedPolicyAsync();
        await RunAsync();

        var instalment = (await InstalmentsAsync())
            .First(i => i.Status == InstalmentStatus.Debiting);

        await SucceedAsync(instalment.DebitCommandId!.Value, "CBS-DEB-7001");

        var reloaded = (await InstalmentsAsync()).First(i => i.Id == instalment.Id);
        reloaded.Status.Should().Be(InstalmentStatus.Paid);
        reloaded.CbsDebitReference.Should().Be("CBS-DEB-7001");
        reloaded.PaidAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_refused_debit_is_retried_on_the_PRODUCT_s_interval()
    {
        // « Une politique paramétrable par produit » — read at decision time, so an administrator
        // raising the limit helps the instalments already failing.
        await SeedPolicyAsync(retryLimit: 3, retryIntervalDays: 5);
        await RunAsync();

        var instalment = (await InstalmentsAsync())
            .First(i => i.Status == InstalmentStatus.Debiting);

        await RejectAsync(instalment.DebitCommandId!.Value, "INSUFFICIENT_FUNDS");

        var reloaded = (await InstalmentsAsync()).First(i => i.Id == instalment.Id);
        reloaded.Status.Should().Be(InstalmentStatus.Failed);
        reloaded.LastErrorCode.Should().Be("INSUFFICIENT_FUNDS");
        reloaded.NextAttemptAt.Should().Be(Now.AddDays(5));
    }

    [Fact]
    public async Task A_product_configured_with_NO_retry_declares_the_impaye_on_the_first_refusal()
    {
        // Zero is a legitimate configuration, and the validator says so in as many words: a
        // product whose unpaid premiums go straight to the insurer. It is also the case that
        // pins the comparison's boundary from below.
        await SeedPolicyAsync(retryLimit: 0);
        await RunAsync();

        var instalment = (await InstalmentsAsync())
            .First(i => i.Status == InstalmentStatus.Debiting);

        await RejectAsync(instalment.DebitCommandId!.Value, "ACCOUNT_CLOSED");

        var reloaded = (await InstalmentsAsync()).First(i => i.Id == instalment.Id);
        reloaded.Status.Should().Be(InstalmentStatus.Unpaid);
        reloaded.UnpaidDeclaredAt.Should().NotBeNull();
        reloaded.NextAttemptAt.Should().BeNull("an unpaid instalment is not retried");
    }

    [Fact]
    public async Task The_impaye_is_declared_once_the_retries_are_spent_and_not_before()
    {
        // The boundary from above, and the only case that drives a SECOND attempt — so it is also
        // what proves the job re-picks up a Failed instalment at all once its interval has run.
        // « Retry limit » counts retries: one retry means two attempts, and the first refusal must
        // not be the impayé.
        await SeedPolicyAsync(retryLimit: 1, retryIntervalDays: 5);
        await RunAsync();

        var first = (await InstalmentsAsync()).First(i => i.Status == InstalmentStatus.Debiting);
        await RejectAsync(first.DebitCommandId!.Value, "INSUFFICIENT_FUNDS");

        (await InstalmentsAsync()).First(i => i.Id == first.Id)
            .Status.Should().Be(InstalmentStatus.Failed, "one retry is still owed");

        _clock.Advance(TimeSpan.FromDays(6));
        _debits.Clear();
        await RunAsync();

        var second = (await InstalmentsAsync()).First(i => i.Id == first.Id);
        second.Status.Should().Be(InstalmentStatus.Debiting);
        second.Attempts.Should().Be(2);
        _debits.Should().ContainSingle(d => d.InstalmentId == first.Id && d.Attempt == 2);

        await RejectAsync(second.DebitCommandId!.Value, "INSUFFICIENT_FUNDS");

        var spent = (await InstalmentsAsync()).First(i => i.Id == first.Id);
        spent.Status.Should().Be(InstalmentStatus.Unpaid);
        spent.NextAttemptAt.Should().BeNull();
    }

    [Fact]
    public async Task A_policy_with_no_product_still_gets_one_retry_rather_than_none()
    {
        // A synchronised policy whose insurer product code has no mapping yet. Refusing to decide
        // would leave the instalment in Debiting for ever, which no job picks up; declaring an
        // impayé on the first refusal would tell the insurer too much too soon about what is a
        // configuration gap on our own side.
        await SeedPolicyAsync(withProduct: false);
        await RunAsync();

        var instalment = (await InstalmentsAsync())
            .First(i => i.Status == InstalmentStatus.Debiting);

        await RejectAsync(instalment.DebitCommandId!.Value, "TIMEOUT");

        var reloaded = (await InstalmentsAsync()).First(i => i.Id == instalment.Id);
        reloaded.Status.Should().Be(InstalmentStatus.Failed);
        reloaded.NextAttemptAt.Should().Be(
            Now.AddDays(PremiumFailedConsumer.FallbackRetryIntervalDays));
    }

    [Fact]
    public async Task A_subscription_s_premium_debit_is_left_to_the_saga()
    {
        // Both chains publish the same event for the same command type. They are told apart by
        // which table stores the command id, never by the event.
        await SeedPolicyAsync();

        var act = async () => await SucceedAsync(Guid.NewGuid(), "CBS-DEB-9999");

        await act.Should().NotThrowAsync();

        (await InstalmentsAsync()).Should()
            .NotContain(i => i.Status == InstalmentStatus.Paid);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task SucceedAsync(Guid commandId, string? externalRef)
    {
        await using var provider = ConsumerContainer();

        await new PremiumPaidConsumer(
                new FixedPremiumScopeFactory(provider),
                NullLogger<PremiumPaidConsumer>.Instance)
            .Consume(Delivered(new IntegrationCommandSucceededEvent(
                Tenant, commandId, Guid.NewGuid(), nameof(CommandType.DebitAccount),
                IntegrationEntityTypes.Account, Customer, externalRef, Now)));
    }

    private async Task RejectAsync(Guid commandId, string code)
    {
        await using var provider = ConsumerContainer();

        await new PremiumFailedConsumer(
                new FixedPremiumScopeFactory(provider),
                NullLogger<PremiumFailedConsumer>.Instance)
            .Consume(Delivered(new IntegrationCommandRejectedEvent(
                Tenant, commandId, Guid.NewGuid(), nameof(CommandType.DebitAccount),
                IntegrationEntityTypes.Account, Customer, "Business", code,
                "Compte insuffisant", 1, Now)));
    }

    private ServiceProvider ConsumerContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IIntegrationInboxGuard>(new GrantingInbox());
        services.AddScoped(_ => _factory.CreateContext());
        return services.BuildServiceProvider();
    }

    private static ConsumeContext<T> Delivered<T>(T evt) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    /// <summary>
    /// Advanceable, because one case has to live through a retry interval: the job only re-picks
    /// up a Failed instalment once <c>NextAttemptAt</c> is in the past, and a frozen clock can
    /// never reach it.
    /// </summary>
    private sealed class MovablePremiumClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class GrantingInbox : IIntegrationInboxGuard
    {
        private readonly HashSet<(Guid, string)> _claimed = [];

        public Task<bool> TryBeginAsync(
            Guid messageId, Guid tenantId, string eventType, string consumerKey, CancellationToken ct)
            => Task.FromResult(_claimed.Add((messageId, consumerKey)));
    }

    private sealed class FixedPremiumScopeFactory(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
