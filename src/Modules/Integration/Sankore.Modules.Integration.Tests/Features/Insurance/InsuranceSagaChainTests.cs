namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Xunit;

/// <summary>
/// The ASS-05 chain once it is running: debit → insurer → (reversal) → settled, and every way it
/// can stop.
///
/// <para>
/// The compensating step is the reason the saga exists at all. An insurer refusing AFTER the
/// premium was collected means the money has to go back, and nothing but a durable row can
/// remember that across a process restart — which is what « l'état global de la souscription
/// consultable » is asking for.
/// </para>
/// </summary>
public sealed class InsuranceSagaChainTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly ICoreBankingGateway _cbs = Substitute.For<ICoreBankingGateway>();
    private readonly IInsuranceGateway _insurer = Substitute.For<IInsuranceGateway>();

    private readonly List<DebitReversalRequest> _reversals = [];
    private readonly List<InsurancePolicyPayload> _subscribes = [];

    private readonly Guid _subscribeCommandId = Guid.NewGuid();
    private readonly Guid _reversalCommandId = Guid.NewGuid();

    public InsuranceSagaChainTests()
    {
        _integration.CoreBanking.Returns(_cbs);
        _integration.Insurance.Returns(_insurer);

        _insurer.RequestPolicySubscriptionAsync(
                Arg.Do<InsurancePolicyPayload>(_subscribes.Add), Arg.Any<CancellationToken>())
            .Returns(_ => new IntegrationCommandId(_subscribeCommandId));

        _cbs.RequestDebitReversalAsync(
                Arg.Do<DebitReversalRequest>(_reversals.Add), Arg.Any<CancellationToken>())
            .Returns(_ => new IntegrationCommandId(_reversalCommandId));
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>A saga at the step named, with the catalogue entry behind it.</summary>
    private async Task<InsuranceSubscription> SeedSagaAsync(
        Guid debitCommandId,
        bool debited = false,
        string? debitReference = "CBS-DEB-0001",
        string? accountRef = "CI0010001-00042",
        bool productExists = true)
    {
        await using var db = _factory.CreateContext();

        var connection = InsuranceTestHarness.SeedConnection(
            db, Tenant, IntegrationKind.Fake, name: "ORASS Vie");

        var product = InsuranceTestHarness.SeedProduct(db, Tenant, connection.Id);
        await db.SaveChangesAsync();

        var saga = InsuranceSubscription.Create(
            tenantId: Tenant,
            connectionId: connection.Id,
            crmCustomerId: Customer,
            productId: productExists ? product.Id : Guid.NewGuid(),
            effectiveDate: new DateOnly(2026, 7, 1),
            premiumAmount: 7_500m,
            currency: "XOF",
            createdBy: Guid.NewGuid(),
            clock: new FixedChainClock(Now),
            cbsAccountRef: accountRef);

        saga.AttachDebitCommand(debitCommandId, new FixedChainClock(Now));

        if (debited && debitReference is not null)
            saga.MarkDebited(debitReference, new FixedChainClock(Now));

        db.InsuranceSubscriptions.Add(saga);
        await db.SaveChangesAsync();
        return saga;
    }

    private ServiceProvider Container()
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(new FixedChainClock(Now));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IIntegrationInboxGuard>(new AlwaysGrantsInbox());
        services.AddSingleton(_integration);
        services.AddScoped(_ => _factory.CreateContext());

        return services.BuildServiceProvider();
    }

    private async Task SucceedAsync(Guid commandId, CommandType type, string? externalRef)
    {
        await using var provider = Container();

        await new InsuranceSagaSucceededConsumer(
                new FixedScopeFactoryForSaga(provider),
                NullLogger<InsuranceSagaSucceededConsumer>.Instance)
            .Consume(Delivered(new IntegrationCommandSucceededEvent(
                TenantId: Tenant,
                CommandId: commandId,
                ConnectionId: Guid.NewGuid(),
                CommandType: type.ToString(),
                EntityType: IntegrationEntityTypes.Account,
                CrmId: Customer,
                ExternalRef: externalRef,
                SucceededAt: Now)));
    }

    private async Task RejectAsync(
        Guid commandId, CommandType type, string code = "INSURER_REFUSED")
    {
        await using var provider = Container();

        await new InsuranceSagaRejectedConsumer(
                new FixedScopeFactoryForSaga(provider),
                NullLogger<InsuranceSagaRejectedConsumer>.Instance)
            .Consume(Delivered(new IntegrationCommandRejectedEvent(
                TenantId: Tenant,
                CommandId: commandId,
                ConnectionId: Guid.NewGuid(),
                CommandType: type.ToString(),
                EntityType: IntegrationEntityTypes.Policy,
                CrmId: Customer,
                ErrorFamily: "Business",
                ErrorCode: code,
                Detail: "Risque refusé par l'assureur",
                Attempts: 1,
                RejectedAt: Now)));
    }

    private static ConsumeContext<T> Delivered<T>(T evt) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(evt);
        context.MessageId.Returns(Guid.NewGuid());
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private async Task<InsuranceSubscription> ReloadAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.InsuranceSubscriptions.IgnoreQueryFilters().FirstAsync(s => s.Id == id);
    }

    // ── the forward path ────────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_debit_asks_the_insurer_and_nothing_sooner()
    {
        var debitCommandId = Guid.NewGuid();
        var saga = await SeedSagaAsync(debitCommandId);

        await SucceedAsync(debitCommandId, CommandType.DebitAccount, "CBS-DEB-0001");

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.CbsDebitReference.Should().Be("CBS-DEB-0001");
        reloaded.DebitedAt.Should().NotBeNull();
        reloaded.SubscribeCommandId.Should().Be(_subscribeCommandId);

        var payload = _subscribes.Should().ContainSingle().Subject;
        payload.PremiumAmount.Should().Be(7_500m);
        payload.Currency.Should().Be("XOF");

        // The beneficiaries are NOT decrypted into the payload: they are encrypted on the saga,
        // and BNF-B-16's holding route is what sends a clause, reading it from M01.
        payload.Beneficiaries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_debit_acknowledged_with_NO_reference_stops_the_chain()
    {
        // The reversal is keyed on the original reference — the only form a CBS accepts twice
        // safely. Without one the premium could never be refunded, so no policy is subscribed
        // against it.
        var debitCommandId = Guid.NewGuid();
        var saga = await SeedSagaAsync(debitCommandId);

        await SucceedAsync(debitCommandId, CommandType.DebitAccount, externalRef: null);

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.Status.Should().Be(SubscriptionStatus.DebitFailed);
        reloaded.FailureCode.Should().Be("DEBIT_REFERENCE_MISSING");

        await _insurer.DidNotReceiveWithAnyArgs()
            .RequestPolicySubscriptionAsync(default!, default);
    }

    [Fact]
    public async Task An_issued_policy_is_recorded_so_it_shows_without_waiting_for_the_sync()
    {
        var debitCommandId = Guid.NewGuid();
        var saga = await SeedSagaAsync(debitCommandId, debited: true);
        await AttachSubscribeAsync(saga.Id);

        await SucceedAsync(_subscribeCommandId, CommandType.SubscribePolicy, "POL-2026-0042");

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.InsurerPolicyReference.Should().Be("POL-2026-0042");
        reloaded.PolicyId.Should().NotBeNull();

        await using var db = _factory.CreateContext();
        var policy = db.Policies.IgnoreQueryFilters().Single();
        policy.ExternalPolicyId.Should().Be("POL-2026-0042");
        policy.SubscriptionId.Should().Be(saga.Id);
        policy.Status.Should().Be(PolicyStatus.Issued);
    }

    [Fact]
    public async Task An_acceptance_with_no_policy_reference_leaves_the_saga_where_it_is()
    {
        // The same rule BNF-B-16 applies to a holding: the reference is the only link between the
        // two systems, and a policy without one is irreconcilable. Deliberately not a FAILURE —
        // the premium is collected and the insurer said yes; the number is what ASS-07's
        // synchronisation can still bring.
        var debitCommandId = Guid.NewGuid();
        var saga = await SeedSagaAsync(debitCommandId, debited: true);
        await AttachSubscribeAsync(saga.Id);

        await SucceedAsync(_subscribeCommandId, CommandType.SubscribePolicy, externalRef: null);

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.PolicyId.Should().BeNull();
        // Debited, which is where MarkDebited left it: there is no separate "waiting on the
        // insurer" status, and the attached SubscribeCommandId is what says the insurer was asked.
        reloaded.Status.Should().Be(SubscriptionStatus.Debited);
        reloaded.SubscribeCommandId.Should().NotBeNull();

        await using var db = _factory.CreateContext();
        db.Policies.IgnoreQueryFilters().Should().BeEmpty();
    }

    // ── the compensation ────────────────────────────────────────────────────

    [Fact]
    public async Task An_insurer_refusal_AFTER_the_debit_queues_a_reversal()
    {
        var debitCommandId = Guid.NewGuid();
        var saga = await SeedSagaAsync(debitCommandId, debited: true);
        await AttachSubscribeAsync(saga.Id);

        await RejectAsync(_subscribeCommandId, CommandType.SubscribePolicy);

        var reversal = _reversals.Should().ContainSingle().Subject;
        reversal.OriginalReference.Should().Be("CBS-DEB-0001",
            "a reversal is keyed on the reference the debit returned");
        reversal.Attempt.Should().Be(1);

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.ReversalCommandId.Should().Be(_reversalCommandId);
        reloaded.ReversalAttempts.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_debit_compensates_nothing_because_nothing_moved()
    {
        var debitCommandId = Guid.NewGuid();
        var saga = await SeedSagaAsync(debitCommandId);

        await RejectAsync(debitCommandId, CommandType.DebitAccount, "INSUFFICIENT_FUNDS");

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.Status.Should().Be(SubscriptionStatus.DebitFailed);
        reloaded.FailureCode.Should().Be("INSUFFICIENT_FUNDS");

        _reversals.Should().BeEmpty();
        await _insurer.DidNotReceiveWithAnyArgs()
            .RequestPolicySubscriptionAsync(default!, default);
    }

    [Fact]
    public async Task A_successful_reversal_settles_the_saga()
    {
        var saga = await SeedReversingAsync();

        await SucceedAsync(_reversalCommandId, CommandType.ReverseDebit, "CBS-REV-0001");

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.CbsReversalReference.Should().Be("CBS-REV-0001");
        reloaded.ReversedAt.Should().NotBeNull();
        reloaded.OwesRefund.Should().BeFalse();
    }

    [Fact]
    public async Task A_REFUSED_reversal_leaves_the_customer_owed_and_says_so()
    {
        // Not retried automatically, deliberately: a refund the CBS has already refused will be
        // refused again for the same reason, and a retry loop would bury the fact that a customer
        // is owed money. PremiumRefundDue is the state an operator works from.
        var saga = await SeedReversingAsync();

        await RejectAsync(_reversalCommandId, CommandType.ReverseDebit, "ACCOUNT_CLOSED");

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.OwesRefund.Should().BeTrue();
        reloaded.Status.Should().Be(SubscriptionStatus.PremiumRefundDue);
        reloaded.FailureCode.Should().Be("ACCOUNT_CLOSED");
    }

    [Fact]
    public async Task A_saga_that_reached_the_insurer_ALWAYS_has_a_debit_reference()
    {
        // This replaces a case that tried to drive "the insurer refused and there is no debit
        // reference to key a reversal on". That state cannot be constructed, and the attempt is
        // what proved it: three rules of the aggregate make the consumer's guard for it dead code.
        //
        // Pinned here rather than deleted, because the guard IS kept as defence in depth — so the
        // day one of these rules is relaxed, this test fails and the comment claiming the branch
        // is unreachable stops being a claim nobody checks.
        var clock = new FixedChainClock(Now);

        var saga = InsuranceSubscription.Create(
            tenantId: Tenant, connectionId: Guid.NewGuid(), crmCustomerId: Customer,
            productId: Guid.NewGuid(), effectiveDate: new DateOnly(2026, 7, 1),
            premiumAmount: 7_500m, currency: "XOF", createdBy: Guid.NewGuid(), clock: clock,
            cbsAccountRef: "CI0010001-00042");

        saga.AttachDebitCommand(Guid.NewGuid(), clock);

        // 1. The insurer cannot be asked before the premium is debited.
        var tooEarly = () => saga.AttachSubscribeCommand(Guid.NewGuid(), clock);
        tooEarly.Should().Throw<Sankore.Shared.Kernel.DomainException>();

        // 2. A debit cannot be recorded without the reference a reversal is keyed on.
        var noReference = () => saga.MarkDebited("   ", clock);
        noReference.Should().Throw<Sankore.Shared.Kernel.DomainException>();

        // 3. So by the time the insurer can be asked, the reference is there.
        saga.MarkDebited("CBS-DEB-0001", clock);
        saga.AttachSubscribeCommand(Guid.NewGuid(), clock);
        saga.CbsDebitReference.Should().Be("CBS-DEB-0001");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_product_that_vanished_after_the_debit_sends_the_premium_BACK()
    {
        // The premium has moved, so the chain cannot simply stop. Recording a DEBT would be the
        // wrong remedy — MarkRefundDue is reachable only from Reversing, because a refund never
        // asked of the core banking system is a refund nobody is processing. The money goes back
        // the same way an insurer refusal sends it back.
        var debitCommandId = Guid.NewGuid();
        var saga = await SeedSagaAsync(debitCommandId, productExists: false);

        await SucceedAsync(debitCommandId, CommandType.DebitAccount, "CBS-DEB-0001");

        var reversal = _reversals.Should().ContainSingle().Subject;
        reversal.OriginalReference.Should().Be("CBS-DEB-0001");

        var reloaded = await ReloadAsync(saga.Id);
        reloaded.Status.Should().Be(SubscriptionStatus.Reversing);
        reloaded.FailureCode.Should().Be("INSURANCE_PRODUCT_NOT_FOUND");

        await _insurer.DidNotReceiveWithAnyArgs()
            .RequestPolicySubscriptionAsync(default!, default);
    }

    // ── commands that are nothing to do with a saga ─────────────────────────

    [Fact]
    public async Task A_command_no_saga_owns_is_left_alone()
    {
        // Every command of the platform publishes these events, including the onboarding chain's
        // and BNF-B-16's holdings. This is the common case, not a miss.
        await SeedSagaAsync(Guid.NewGuid());

        var act = async () => await SucceedAsync(
            Guid.NewGuid(), CommandType.DebitAccount, "CBS-DEB-9999");

        await act.Should().NotThrowAsync();
        _subscribes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_command_of_another_TYPE_is_ignored_before_the_inbox()
    {
        // Cheap discriminator first: writing an inbox row for every CreateCustomer of the
        // platform would grow a table for no reason.
        var inbox = new AlwaysGrantsInbox();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedChainClock(Now));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IIntegrationInboxGuard>(inbox);
        services.AddSingleton(_integration);
        services.AddScoped(_ => _factory.CreateContext());

        await using var provider = services.BuildServiceProvider();

        await new InsuranceSagaSucceededConsumer(
                new FixedScopeFactoryForSaga(provider),
                NullLogger<InsuranceSagaSucceededConsumer>.Instance)
            .Consume(Delivered(new IntegrationCommandSucceededEvent(
                Tenant, Guid.NewGuid(), Guid.NewGuid(),
                nameof(CommandType.CreateCustomer), IntegrationEntityTypes.Customer,
                Customer, "CBS-CUS-1", Now)));

        inbox.Claims.Should().Be(0);
    }

    [Fact]
    public async Task The_SYSTEM_placeholder_tenant_is_skipped()
    {
        var inbox = new AlwaysGrantsInbox();

        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationInboxGuard>(inbox);
        await using var provider = services.BuildServiceProvider();

        await new InsuranceSagaSucceededConsumer(
                new FixedScopeFactoryForSaga(provider),
                NullLogger<InsuranceSagaSucceededConsumer>.Instance)
            .Consume(Delivered(new IntegrationCommandSucceededEvent(
                Guid.Empty, Guid.NewGuid(), Guid.NewGuid(),
                nameof(CommandType.DebitAccount), IntegrationEntityTypes.Account,
                Customer, "X", Now)));

        inbox.Claims.Should().Be(0);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task AttachSubscribeAsync(Guid sagaId)
    {
        await using var db = _factory.CreateContext();
        var saga = await db.InsuranceSubscriptions.AsTracking().IgnoreQueryFilters()
            .FirstAsync(s => s.Id == sagaId);

        saga.AttachSubscribeCommand(_subscribeCommandId, new FixedChainClock(Now));
        await db.SaveChangesAsync();
    }

    /// <summary>A saga whose premium is debited, refused by the insurer, and being reversed.</summary>
    private async Task<InsuranceSubscription> SeedReversingAsync()
    {
        var saga = await SeedSagaAsync(Guid.NewGuid(), debited: true);
        await AttachSubscribeAsync(saga.Id);
        await RejectAsync(_subscribeCommandId, CommandType.SubscribePolicy);
        _reversals.Clear();
        return saga;
    }

    private sealed class FixedChainClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AlwaysGrantsInbox : IIntegrationInboxGuard
    {
        private readonly HashSet<(Guid, string)> _claimed = [];

        internal int Claims { get; private set; }

        public Task<bool> TryBeginAsync(
            Guid messageId, Guid tenantId, string eventType, string consumerKey, CancellationToken ct)
        {
            Claims++;
            return Task.FromResult(_claimed.Add((messageId, consumerKey)));
        }
    }

    private sealed class FixedScopeFactoryForSaga(IServiceProvider sp) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(sp);

        private sealed class Scope(IServiceProvider sp) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = sp;
            public void Dispose() { }
        }
    }
}
