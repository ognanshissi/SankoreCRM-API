namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-10 / ASS-04 / ASS-07. What every insurance adapter must do with a policy.
///
/// <para>
/// Subscription is the write the premium debit is paired with: the orchestration takes the money
/// first and reverses it if the insurer refuses. A subscription replayed under its key must
/// therefore return the SAME policy, or the reversal would be decided against one policy while a
/// second one stays live and unpaid.
/// </para>
/// </summary>
public abstract class InsurancePolicyPortContractTests : IntegrationWritePortContractTests<IInsurancePolicyPort>
{
    protected abstract ExternalId ExistingCustomerId { get; }

    protected abstract ExternalId ExistingPolicyId { get; }

    protected abstract string ExistingProductCode { get; }

    protected virtual InsurancePolicyPayload Payload
        => FakeAdapterFixtures.PolicyPayload(ExistingProductCode);

    protected override Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        IInsurancePolicyPort port, IdempotencyKey key)
        => port.SubscribeAsync(Payload, key, CancellationToken.None);

    protected override async Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(
        IInsurancePolicyPort port)
        => await port.GetPolicyAsync(AbsentId, CancellationToken.None);

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        IInsurancePolicyPort port)
    {
        var result = await port.GetPolicyAsync(ExistingPolicyId, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    [Fact]
    public async Task Subscribing_an_unresolvable_product_code_should_be_a_technical_failure()
    {
        var sut = CreatePort();

        var result = await sut.SubscribeAsync(
            Payload with { InsurerProductCode = UnknownProductCode },
            Key("unknown-insurer-product"),
            CancellationToken.None);

        ShouldBeUnmappedCode(result, UnknownProductCode);
    }

    [Fact]
    public async Task A_subscribed_policy_should_be_readable_and_listed_under_its_holder()
    {
        var sut = CreatePort();

        var subscribed = await sut.SubscribeAsync(
            Payload, Key("subscribe-round-trip"), CancellationToken.None);
        subscribed.IsSuccess.Should().BeTrue();

        var one = await sut.GetPolicyAsync(subscribed.Value, CancellationToken.None);
        var all = await sut.GetPoliciesAsync(ExistingCustomerId, CancellationToken.None);

        one.IsSuccess.Should().BeTrue();
        one.Value.PolicyId.Should().Be(subscribed.Value);
        one.Value.PolicyNumber.Should().NotBeNullOrWhiteSpace(
            "the number is what the customer reads on the certificate; the id is ours");

        all.IsSuccess.Should().BeTrue();
        all.Value.Select(p => p.PolicyId).Should().Contain(subscribed.Value);
    }

    [Fact]
    public async Task The_certificate_of_an_absent_policy_should_be_functional_not_found()
    {
        ShouldBeAbsentEntity(
            await CreatePort().GetCertificateAsync(AbsentId, CancellationToken.None));
    }

    [Fact]
    public async Task A_certificate_should_carry_bytes_and_the_type_they_are_in()
    {
        var sut = CreatePort();

        var certificate = await sut.GetCertificateAsync(ExistingPolicyId, CancellationToken.None);

        certificate.IsSuccess.Should().BeTrue();
        certificate.Value.Content.Should().NotBeEmpty(
            "it is handed to the customer and stored as evidence; an empty document is a lost one");
        certificate.Value.ContentType.Should().NotBeNullOrWhiteSpace();
        certificate.Value.FileName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cancelling_an_absent_policy_should_be_functional_not_found()
    {
        var result = await CreatePort().CancelAsync(
            AbsentId, "Résiliation à la demande du client", Key("cancel-absent"),
            CancellationToken.None);

        ShouldBeAbsentEntity(result);
    }

    [Fact]
    public async Task A_cancelled_policy_should_read_back_as_cancelled()
    {
        var sut = CreatePort();

        var subscribed = await sut.SubscribeAsync(
            Payload, Key("cancel-round-trip"), CancellationToken.None);
        subscribed.IsSuccess.Should().BeTrue();

        var cancelled = await sut.CancelAsync(
            subscribed.Value, "Résiliation à la demande du client", Key("cancel-do"),
            CancellationToken.None);
        cancelled.IsSuccess.Should().BeTrue();

        var reread = await sut.GetPolicyAsync(subscribed.Value, CancellationToken.None);

        // A cancellation the insurer accepted but does not reflect would leave the CRM billing a
        // premium for a policy that no longer covers anyone.
        reread.IsSuccess.Should().BeTrue();
        reread.Value.Status.Should().Be(PolicyStatus.Cancelled);
    }
}
