namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-10. What every core banking adapter must do with a loan application.
///
/// <para>
/// The idempotency fact matters more here than anywhere else: a loan application submitted twice
/// is two files in the credit committee's queue for one customer, and the second one is the kind
/// of duplicate a committee approves before anybody notices.
/// </para>
/// </summary>
public abstract class CbsLoanPortContractTests : IntegrationWritePortContractTests<ICbsLoanPort>
{
    protected abstract ExternalId ExistingCustomerId { get; }

    protected abstract string ExistingProductCode { get; }

    protected virtual CbsLoanApplicationPayload Application
        => FakeAdapterFixtures.LoanApplication(ExistingCustomerId, ExistingProductCode);

    protected override Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        ICbsLoanPort port, IdempotencyKey key)
        => port.SubmitLoanApplicationAsync(Application, key, CancellationToken.None);

    protected override async Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(ICbsLoanPort port)
        => await port.GetLoansAsync(AbsentId, CancellationToken.None);

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        ICbsLoanPort port)
    {
        var result = await port.GetLoansAsync(ExistingCustomerId, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    [Fact]
    public async Task An_unresolvable_product_code_should_be_a_technical_failure_naming_it()
    {
        var sut = CreatePort();

        var result = await sut.SubmitLoanApplicationAsync(
            Application with { ProductCode = UnknownProductCode },
            Key("unknown-loan-product"),
            CancellationToken.None);

        ShouldBeUnmappedCode(result, UnknownProductCode);
    }

    [Fact]
    public async Task Submitting_for_an_absent_customer_should_be_functional_not_found()
    {
        var sut = CreatePort();

        var result = await sut.SubmitLoanApplicationAsync(
            Application with { CustomerId = AbsentId },
            Key("absent-borrower"),
            CancellationToken.None);

        ShouldBeAbsentEntity(result);
    }

    [Fact]
    public async Task A_submitted_application_should_be_listed_under_its_borrower()
    {
        var sut = CreatePort();

        var submitted = await sut.SubmitLoanApplicationAsync(
            Application, Key("loan-round-trip"), CancellationToken.None);
        submitted.IsSuccess.Should().BeTrue();

        var loans = await sut.GetLoansAsync(ExistingCustomerId, CancellationToken.None);

        loans.IsSuccess.Should().BeTrue();
        loans.Value.Select(l => l.LoanId).Should().Contain(submitted.Value,
            "a file the CBS accepted but does not list is a file the agent cannot follow up");
    }
}
