namespace Sankore.Modules.Integration.Tests.Adapters;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-10, acceptance criterion 1 — the properties of the double itself, which the shared contract
/// suite cannot see.
///
/// <para>
/// The contract suite asserts what every adapter owes its callers. These facts assert what the
/// DOUBLE owes its own users: that it stands in for both families at once, that it answers the
/// same thing twice, that its failure knobs do what they claim, and that it says what it was
/// asked. Every one of them protects a test elsewhere in the module — a double whose answers
/// drift turns a green suite into a suite that has stopped measuring anything.
/// </para>
/// </summary>
public sealed class FakeAdapterTests
{
    [Fact]
    public void The_double_should_stand_in_for_every_port_of_both_families()
    {
        var sut = new FakeAdapter();

        // Acceptance criterion 1, asserted rather than trusted to the class declaration: the
        // resolver hands a caller a port by casting the adapter, so an interface dropped during a
        // refactor surfaces as CAPABILITY_NOT_SUPPORTED at run time, not as a compile error here.
        sut.Should().BeAssignableTo<ICbsAdapter>();
        sut.Should().BeAssignableTo<ICbsCustomerPort>();
        sut.Should().BeAssignableTo<ICbsAccountPort>();
        sut.Should().BeAssignableTo<ICbsTransactionPort>();
        sut.Should().BeAssignableTo<ICbsLoanPort>();
        sut.Should().BeAssignableTo<IInsuranceProductPort>();
        sut.Should().BeAssignableTo<IInsurancePolicyPort>();
        sut.Should().BeAssignableTo<IInsuranceClaimPort>();
        sut.Kind.Should().Be(IntegrationKind.Fake);
    }

    [Fact]
    public void Every_capability_should_be_declared_in_real_time_by_default()
    {
        var sut = new FakeAdapter();

        foreach (var capability in Enum.GetValues<IntegrationCapability>())
        {
            sut.Capabilities.Supports(capability).Should().BeTrue($"{capability} has a port method");
            sut.Capabilities.ModeOf(capability).Should().Be(CapabilityMode.RealTime);
        }
    }

    [Fact]
    public void A_batch_only_double_should_declare_the_same_capabilities_in_batch_mode()
    {
        var sut = FakeAdapter.BatchOnly();

        // What an Amplitude Legacy or a Perfect Vision installation looks like. The capability is
        // still SUPPORTED — a screen must hide the live button and keep offering the command, not
        // decide the operation does not exist.
        sut.Capabilities.Supports(IntegrationCapability.ReadBalance).Should().BeTrue();
        sut.Capabilities.IsRealTime(IntegrationCapability.ReadBalance).Should().BeFalse();
        sut.Capabilities.ModeOf(IntegrationCapability.ReadBalance).Should().Be(CapabilityMode.Batch);
    }

    [Fact]
    public async Task Two_instances_should_answer_identically_to_the_same_call_sequence()
    {
        var first = new FakeAdapter();
        var second = new FakeAdapter();

        var firstAnswers = await WalkAsync(first);
        var secondAnswers = await WalkAsync(second);

        // The whole reason the double reads no clock and rolls no dice. A single varying field
        // here would make every assertion in the module pass or fail depending on the machine.
        secondAnswers.Should().BeEquivalentTo(firstAnswers);
    }

    [Fact]
    public async Task A_replayed_write_should_record_no_second_call()
    {
        var sut = new FakeAdapter();
        var key = new IdempotencyKey("replay");

        var first = await sut.CreateCustomerAsync(
            FakeAdapterFixtures.CustomerPayload(), key, CancellationToken.None);
        var second = await sut.CreateCustomerAsync(
            FakeAdapterFixtures.CustomerPayload(), key, CancellationToken.None);

        second.Value.Should().Be(first.Value);
        sut.Calls.Should().ContainSingle(c => c.Operation == FakeAdapterOperations.CreateCustomer,
            "the silence in the journal is what proves the far end was not touched twice");
    }

    [Fact]
    public async Task A_failed_write_should_not_be_remembered_as_answered()
    {
        var sut = new FakeAdapter();
        var key = new IdempotencyKey("failed-then-retried");

        // An unmapped product refuses; the mapping is then configured and the command replayed.
        var refused = await sut.OpenAccountAsync(
            new ExternalId(FakeAdapter.SeededCustomerId), "PRODUIT-INCONNU", key,
            CancellationToken.None);
        sut.ProductCodes.Add("PRODUIT-INCONNU");
        var retried = await sut.OpenAccountAsync(
            new ExternalId(FakeAdapter.SeededCustomerId), "PRODUIT-INCONNU", key,
            CancellationToken.None);

        refused.IsFailure.Should().BeTrue();

        // Caching the key on failure would turn the replay into a success carrying no account:
        // the command would reach Succeeded with nothing opened. Only a success is remembered.
        retried.IsSuccess.Should().BeTrue();
        retried.Value.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task Rejecting_should_refuse_every_call_on_the_merits()
    {
        var sut = FakeAdapter.Rejecting(IntegrationErrors.DocumentRefused);

        var write = await sut.CreateCustomerAsync(
            FakeAdapterFixtures.CustomerPayload(), new IdempotencyKey("r1"), CancellationToken.None);
        var read = await sut.GetBalanceAsync(
            new ExternalId(FakeAdapter.SeededAccountId), CancellationToken.None);

        write.Family.Should().Be(ErrorFamily.Functional);
        write.Code.Should().Be(IntegrationErrors.DocumentRefused);
        read.Family.Should().Be(ErrorFamily.Functional);
        write.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Unavailable_should_fail_transiently_so_nothing_is_parked_for_a_human()
    {
        var sut = FakeAdapter.Unavailable();

        var result = await sut.GetAccountsAsync(
            new ExternalId(FakeAdapter.SeededCustomerId), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Transient);
        result.Code.Should().Be(IntegrationErrors.Unavailable);
        result.IsRetryable.Should().BeTrue(
            "recording our own outage as a refusal empties onto a human a queue that would have " +
            "drained itself");
    }

    [Fact]
    public async Task Duplicating_should_refuse_the_writes_and_leave_the_reads_answering()
    {
        var sut = FakeAdapter.Duplicating();

        var write = await sut.SubscribeAsync(
            FakeAdapterFixtures.PolicyPayload(FakeAdapter.SeededInsurerProductCode),
            new IdempotencyKey("d1"), CancellationToken.None);
        var read = await sut.GetPolicyAsync(
            new ExternalId(FakeAdapter.SeededPolicyId), CancellationToken.None);

        write.Code.Should().Be(IntegrationErrors.Duplicate);

        // The asymmetry is the point: the rejection queue is inspected against a system whose
        // reads still work, which is exactly the situation a duplicate describes.
        read.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_health_check_should_pass_with_no_forced_outcome()
    {
        var sut = new FakeAdapter();
        var connection = FakeAdapterFixtures.Connection();

        var health = await sut.CheckHealthAsync(connection, CancellationToken.None);

        health.IsHealthy.Should().BeTrue();
        health.Latency.Should().Be(sut.HealthLatency);
        health.CheckedAt.Should().Be(sut.FixedInstant, "a measured instant would not be assertable");
        sut.LastHealthConnection.Should().BeSameAs(connection);
    }

    [Fact]
    public async Task A_health_check_should_fail_when_a_forced_outcome_says_the_far_end_is_down()
    {
        var sut = FakeAdapter.Unavailable();

        var health = await sut.CheckHealthAsync(
            FakeAdapterFixtures.Connection(), CancellationToken.None);

        // INT-03 refuses to activate a connection whose last check failed, so the double has to be
        // able to produce that state or the activation guard is untestable.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Be(IntegrationErrors.Unavailable);
    }

    [Fact]
    public async Task A_health_check_should_honour_the_error_the_connection_row_itself_forces()
    {
        var sut = new FakeAdapter();
        var connection = FakeAdapterFixtures.Connection(new FakeSettings
        {
            ForcedErrorCode = IntegrationErrors.AuthenticationRefused,
            ForcedErrorFamily = ErrorFamily.Technical,
        });

        var health = await sut.CheckHealthAsync(connection, CancellationToken.None);

        // CheckHealthAsync is the only method that receives a connection, so it is the only place
        // the per-connection forcing FakeSettings advertises can be honoured at all.
        health.IsHealthy.Should().BeFalse();
        health.Detail.Should().Be(IntegrationErrors.AuthenticationRefused);
    }

    [Fact]
    public async Task The_double_should_report_what_it_was_asked()
    {
        var sut = new FakeAdapter();
        var payload = FakeAdapterFixtures.CustomerPayload();
        var key = new IdempotencyKey("recorded");

        await sut.CreateCustomerAsync(payload, key, CancellationToken.None);
        await sut.SetKycLevelAsync(
            new ExternalId(FakeAdapter.SeededCustomerId), KycLevel.Full,
            new IdempotencyKey("recorded-kyc"), CancellationToken.None);

        sut.LastCustomerPayload.Should().BeSameAs(payload);
        sut.LastIdempotencyKey.Should().Be(new IdempotencyKey("recorded-kyc"));
        sut.KycLevels[FakeAdapter.SeededCustomerId].Should().Be(KycLevel.Full);
        sut.Calls.Select(c => c.Operation).Should().Equal(
            FakeAdapterOperations.CreateCustomer, FakeAdapterOperations.SetKycLevel);
    }

    [Fact]
    public async Task A_debit_should_move_the_balance_and_a_reversal_should_put_it_back()
    {
        var sut = new FakeAdapter();
        var account = new ExternalId(FakeAdapter.SeededAccountId);
        var before = (await sut.GetBalanceAsync(account, CancellationToken.None)).Value.Balance;

        var debit = await sut.DebitAccountAsync(
            account, 7_500m, FakeAdapter.SeededCurrency, "Prime assurance",
            new IdempotencyKey("premium"), CancellationToken.None);
        var afterDebit = (await sut.GetBalanceAsync(account, CancellationToken.None)).Value.Balance;

        await sut.ReverseDebitAsync(
            account, debit.Value.Reference, new IdempotencyKey("premium-reversal"),
            CancellationToken.None);
        var afterReversal = (await sut.GetBalanceAsync(account, CancellationToken.None)).Value.Balance;

        // The insurance orchestration debits BEFORE the insurer accepts and reverses on a refusal.
        // A double whose balance never moved would let that whole path pass without ever showing
        // the money coming back.
        afterDebit.Should().Be(before - 7_500m);
        afterReversal.Should().Be(before);
    }

    [Fact]
    public async Task A_cancelled_token_should_be_honoured_rather_than_answered()
    {
        var sut = new FakeAdapter();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.GetAccountsAsync(
            new ExternalId(FakeAdapter.SeededCustomerId), cts.Token);

        // A double that ignores the token lets a test pass against a shutdown path a real adapter
        // would have aborted.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// One pass over both families, reduced to comparable values. Shared by the determinism fact
    /// so the two instances are walked by the same code rather than by two copies of it.
    /// </summary>
    private static async Task<object[]> WalkAsync(FakeAdapter sut)
    {
        var customer = await sut.CreateCustomerAsync(
            FakeAdapterFixtures.CustomerPayload(), new IdempotencyKey("walk-customer"),
            CancellationToken.None);
        var account = await sut.OpenAccountAsync(
            customer.Value, FakeAdapter.SeededProductCode, new IdempotencyKey("walk-account"),
            CancellationToken.None);
        var balance = await sut.GetBalanceAsync(account.Value, CancellationToken.None);
        var transactions = await sut.GetTransactionsAsync(
            new ExternalId(FakeAdapter.SeededAccountId), new DateOnly(2024, 1, 1),
            new DateOnly(2024, 12, 31), null, CancellationToken.None);
        var quote = await sut.PriceAsync(
            FakeAdapter.SeededInsurerProductCode, FakeAdapterFixtures.CrmCustomerId, 1_000_000m,
            CancellationToken.None);
        var policy = await sut.SubscribeAsync(
            FakeAdapterFixtures.PolicyPayload(FakeAdapter.SeededInsurerProductCode),
            new IdempotencyKey("walk-policy"), CancellationToken.None);
        var claim = await sut.DeclareAsync(
            FakeAdapterFixtures.ClaimPayload(policy.Value), new IdempotencyKey("walk-claim"),
            CancellationToken.None);
        var health = await sut.CheckHealthAsync(
            FakeAdapterFixtures.Connection(), CancellationToken.None);

        return
        [
            customer.Value, account.Value, balance.Value, transactions.Value,
            quote.Value, policy.Value, claim.Value, health,
        ];
    }
}
