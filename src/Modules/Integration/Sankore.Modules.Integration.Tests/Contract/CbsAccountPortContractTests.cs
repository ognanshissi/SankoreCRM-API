namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-10 / INT-13 / ASS-05. What every core banking adapter must do with an account — and with
/// the money on it.
///
/// <para>
/// The debit facts are the sharpest in the whole contract: the insurance orchestration debits the
/// premium BEFORE the insurer accepts, so a debit that is retried because an adapter mislabelled
/// "not enough money" as transient takes the premium twice from a customer who was refused a
/// policy. The family of a refusal is therefore asserted, not documented.
/// </para>
/// </summary>
public abstract class CbsAccountPortContractTests : IntegrationWritePortContractTests<ICbsAccountPort>
{
    /// <summary>A customer the far end holds, under which this suite may open accounts.</summary>
    protected abstract ExternalId ExistingCustomerId { get; }

    /// <summary>An account the far end holds, operable and readable.</summary>
    protected abstract ExternalId ExistingAccountId { get; }

    /// <summary>A product code that resolves through the adapter's mapping table.</summary>
    protected abstract string ExistingProductCode { get; }

    /// <summary>
    /// More than any seeded account holds. Deliberately absurd rather than "balance + 1": the
    /// suite must not have to read a balance to build an over-draw, and a sandbox's balances move.
    /// </summary>
    protected virtual decimal AmountBeyondAnyBalance => 999_999_999_999m;

    protected override Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        ICbsAccountPort port, IdempotencyKey key)
        => port.OpenAccountAsync(ExistingCustomerId, ExistingProductCode, key, CancellationToken.None);

    protected override async Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(ICbsAccountPort port)
        => await port.GetBalanceAsync(AbsentId, CancellationToken.None);

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        ICbsAccountPort port)
    {
        var result = await port.GetBalanceAsync(ExistingAccountId, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    [Fact]
    public async Task An_unresolvable_product_code_should_be_a_technical_failure_naming_it()
    {
        var sut = CreatePort();

        var result = await sut.OpenAccountAsync(
            ExistingCustomerId, UnknownProductCode, Key("unknown-product"), CancellationToken.None);

        ShouldBeUnmappedCode(result, UnknownProductCode);
    }

    [Fact]
    public async Task Opening_an_account_for_an_absent_customer_should_be_functional_not_found()
    {
        var sut = CreatePort();

        var result = await sut.OpenAccountAsync(
            AbsentId, ExistingProductCode, Key("absent-holder"), CancellationToken.None);

        ShouldBeAbsentEntity(result);
    }

    [Fact]
    public async Task A_newly_opened_account_should_be_listed_and_readable_under_its_reference()
    {
        var sut = CreatePort();

        var opened = await sut.OpenAccountAsync(
            ExistingCustomerId, ExistingProductCode, Key("open-round-trip"), CancellationToken.None);
        opened.IsSuccess.Should().BeTrue();

        var accounts = await sut.GetAccountsAsync(ExistingCustomerId, CancellationToken.None);
        var balance = await sut.GetBalanceAsync(opened.Value, CancellationToken.None);

        accounts.IsSuccess.Should().BeTrue();
        accounts.Value.Select(a => a.AccountId).Should().Contain(opened.Value,
            "Customer 360 lists accounts from the customer, so one that opened but does not list " +
            "is invisible to the counter that just created it");
        balance.IsSuccess.Should().BeTrue();
        balance.Value.AccountId.Should().Be(opened.Value);
    }

    [Fact]
    public async Task A_read_balance_should_say_when_it_was_true()
    {
        var sut = CreatePort();

        var balance = await sut.GetBalanceAsync(ExistingAccountId, CancellationToken.None);

        balance.IsSuccess.Should().BeTrue();
        balance.Value.AsOf.Should().NotBe(default(DateTimeOffset),
            "a figure shown at a counter without an instant cannot be told apart from a stale one");
        balance.Value.Currency.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_debit_beyond_the_balance_should_be_functional_so_it_is_never_retried()
    {
        var sut = CreatePort();

        var result = await sut.DebitAccountAsync(
            ExistingAccountId, AmountBeyondAnyBalance, "XOF", "Prime assurance",
            Key("over-draw"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Functional,
            "a retried over-draw debits the premium the moment a salary lands, which is not a " +
            "decision an adapter gets to take on the customer's behalf");
        result.Code.Should().BeOneOf(
            IntegrationErrors.InsufficientFunds, IntegrationErrors.AccountNotOperable);
    }

    [Fact]
    public async Task A_debit_replayed_under_the_same_key_should_post_once()
    {
        var sut = CreatePort();
        var key = Key("debit-idempotency");

        var first = await sut.DebitAccountAsync(
            ExistingAccountId, 1_000m, "XOF", "Prime assurance", key, CancellationToken.None);
        var second = await sut.DebitAccountAsync(
            ExistingAccountId, 1_000m, "XOF", "Prime assurance", key, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Reference.Should().Be(first.Value.Reference,
            "the reference IS the movement: two of them is two debits, and the second is one the " +
            "customer never agreed to");
        second.Value.Amount.Should().Be(first.Value.Amount);
    }

    [Fact]
    public async Task Reversing_a_reference_the_far_end_never_issued_should_be_functional_not_found()
    {
        var sut = CreatePort();

        var result = await sut.ReverseDebitAsync(
            ExistingAccountId, "SANKORE-CONTRACT-UNISSUED-REFERENCE", Key("reverse-unknown"),
            CancellationToken.None);

        // Not transient: a reversal naming a movement that does not exist cannot start existing.
        ShouldBeAbsentEntity(result);
    }
}
