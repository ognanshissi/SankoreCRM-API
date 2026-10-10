namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-10 / ASS-02 / ASS-03 / ASS-04. What every insurance adapter must do with a product.
///
/// <para>
/// The only port that addresses no entity: its three methods take an insurer product code, so an
/// unknown one is a mapping failure and never a missing record. That is why this suite inherits
/// the bare port contract and not the entity one — the question "what does an absent entity look
/// like" cannot be put to it, and a suite forced to answer it would have invented a verdict.
/// </para>
/// </summary>
public abstract class InsuranceProductPortContractTests : IntegrationPortContractTests<IInsuranceProductPort>
{
    /// <summary>An insurer product code the adapter can resolve.</summary>
    protected abstract string ExistingProductCode { get; }

    protected virtual Guid CrmCustomerId => TestSupport.FakeAdapterFixtures.CrmCustomerId;

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        IInsuranceProductPort port)
    {
        var result = await port.PriceAsync(
            ExistingProductCode, CrmCustomerId, insuredAmount: null, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    [Fact]
    public async Task Pricing_an_unresolvable_code_should_be_a_technical_failure_naming_it()
    {
        var sut = CreatePort();

        var result = await sut.PriceAsync(
            UnknownProductCode, CrmCustomerId, insuredAmount: null, CancellationToken.None);

        ShouldBeUnmappedCode(result, UnknownProductCode);
    }

    [Fact]
    public async Task Checking_eligibility_on_an_unresolvable_code_should_be_a_technical_failure()
    {
        var sut = CreatePort();

        var result = await sut.CheckEligibilityAsync(
            UnknownProductCode, CrmCustomerId, CancellationToken.None);

        ShouldBeUnmappedCode(result, UnknownProductCode);
    }

    [Fact]
    public async Task A_catalogue_should_either_list_codes_or_say_it_has_none_to_expose()
    {
        var sut = CreatePort();

        var result = await sut.GetProductCodesAsync(CancellationToken.None);

        if (result.IsFailure)
        {
            // A tenant that configures its products by hand needs no catalogue. Saying so is a
            // legitimate answer — but it has to be the CapabilityNotSupported one, not an empty
            // success a screen would render as "this insurer sells nothing".
            result.Family.Should().Be(ErrorFamily.Technical);
            result.Code.Should().Be(IntegrationErrors.CapabilityNotSupported);
            return;
        }

        result.Value.Should().Contain(ExistingProductCode,
            "a catalogue that omits a code the same adapter prices contradicts itself");
    }

    [Fact]
    public async Task A_price_should_name_the_product_it_was_asked_about()
    {
        var sut = CreatePort();

        var quote = await sut.PriceAsync(
            ExistingProductCode, CrmCustomerId, insuredAmount: null, CancellationToken.None);

        quote.IsSuccess.Should().BeTrue();
        quote.Value.InsurerProductCode.Should().Be(ExistingProductCode,
            "a quote is stored against a product; one that renames it cannot be reconciled");
        quote.Value.PremiumAmount.Should().BeGreaterThan(0);
        quote.Value.Currency.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_refused_eligibility_should_carry_the_reasons_the_agent_has_to_explain()
    {
        var sut = CreatePort();

        var verdict = await sut.CheckEligibilityAsync(
            ExistingProductCode, CrmCustomerId, CancellationToken.None);

        verdict.IsSuccess.Should().BeTrue();

        // Asserted as an invariant rather than against a configured refusal: whichever way the
        // adapter answers, "not eligible" with no reason sends the customer away without knowing
        // why, which is the one outcome ASS-04 forbids.
        if (!verdict.Value.IsEligible)
        {
            verdict.Value.Reasons.Should().NotBeEmpty();
        }
    }
}
