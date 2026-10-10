namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-10 / INT-12. What every core banking adapter must do with a customer.
///
/// <para>
/// Onboarding is the one flow whose failure is invisible: a customer created twice in the CBS
/// looks like a success on both sides until an account is opened on the wrong reference. The
/// facts below are the ones the dispatcher's retry depends on, so they are asserted here once and
/// every adapter inherits them.
/// </para>
/// </summary>
public abstract class CbsCustomerPortContractTests : IntegrationWritePortContractTests<ICbsCustomerPort>
{
    /// <summary>
    /// A complete CRM customer. Virtual so an adapter whose sandbox refuses a field (a nationality
    /// outside its list, say) can narrow it without the suite losing its meaning.
    /// </summary>
    protected virtual CbsCustomerPayload Payload => FakeAdapterFixtures.CustomerPayload();

    protected override Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        ICbsCustomerPort port, IdempotencyKey key)
        => port.CreateCustomerAsync(Payload, key, CancellationToken.None);

    protected override async Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(ICbsCustomerPort port)
        => await port.SetKycLevelAsync(AbsentId, KycLevel.Full, Key("absent-kyc"), CancellationToken.None);

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        ICbsCustomerPort port)
    {
        var result = await port.CreateCustomerAsync(Payload, Key("valued"), CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    [Fact]
    public async Task Updating_an_absent_customer_should_be_a_functional_failure_not_a_creation()
    {
        var sut = CreatePort();

        var result = await sut.UpdateCustomerAsync(
            AbsentId, Payload, Key("absent-update"), CancellationToken.None);

        // An update that falls back to a create is how a typo in a reference ends up as a second
        // customer file nobody reconciles. It must refuse.
        ShouldBeAbsentEntity(result);
    }

    [Fact]
    public async Task A_created_customer_should_be_reachable_under_the_reference_it_returned()
    {
        var sut = CreatePort();

        var created = await sut.CreateCustomerAsync(Payload, Key("round-trip"), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var update = await sut.UpdateCustomerAsync(
            created.Value, Payload, Key("round-trip-update"), CancellationToken.None);
        var grade = await sut.SetKycLevelAsync(
            created.Value, KycLevel.Full, Key("round-trip-kyc"), CancellationToken.None);

        // The reference an adapter hands back is written into integration_reference and every
        // later command is addressed with it. One that the adapter itself cannot resolve would
        // make the whole mapping table a set of dead links.
        update.IsSuccess.Should().BeTrue();
        grade.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_returned_reference_should_carry_a_value()
    {
        var created = await CreatingWriteAsync(CreatePort(), Key("non-empty"));

        created.IsSuccess.Should().BeTrue();
        created.Value.HasValue.Should().BeTrue(
            "an empty ExternalId stored as the CBS reference reads back later as a real link to nothing");
    }
}
