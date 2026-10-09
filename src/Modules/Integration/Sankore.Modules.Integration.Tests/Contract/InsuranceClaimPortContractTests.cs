namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-10 / ASS-09. What every insurance adapter must do with a claim.
///
/// <para>
/// A claim is declared once, by a bereaved family at a counter, and then followed for weeks. Two
/// claim numbers for one event is the failure the agent discovers when the insurer indemnifies
/// one of them and refuses the other — so the idempotency fact is inherited, and the follow-up
/// reads are asserted to resolve the number the declaration handed back.
/// </para>
/// </summary>
public abstract class InsuranceClaimPortContractTests : IntegrationWritePortContractTests<IInsuranceClaimPort>
{
    protected abstract ExternalId ExistingPolicyId { get; }

    protected abstract ExternalId ExistingClaimId { get; }

    protected virtual InsuranceClaimPayload Payload => FakeAdapterFixtures.ClaimPayload(ExistingPolicyId);

    protected override Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        IInsuranceClaimPort port, IdempotencyKey key)
        => port.DeclareAsync(Payload, key, CancellationToken.None);

    protected override async Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(
        IInsuranceClaimPort port)
        => await port.GetClaimAsync(AbsentId, CancellationToken.None);

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        IInsuranceClaimPort port)
    {
        var result = await port.GetClaimAsync(ExistingClaimId, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    [Fact]
    public async Task Declaring_against_an_absent_policy_should_be_functional_not_found()
    {
        var sut = CreatePort();

        var result = await sut.DeclareAsync(
            Payload with { PolicyId = AbsentId }, Key("absent-policy"), CancellationToken.None);

        ShouldBeAbsentEntity(result);
    }

    [Fact]
    public async Task A_declared_claim_should_be_readable_and_listed_under_its_policy()
    {
        var sut = CreatePort();

        var declared = await sut.DeclareAsync(Payload, Key("declare-round-trip"), CancellationToken.None);
        declared.IsSuccess.Should().BeTrue();

        var one = await sut.GetClaimAsync(declared.Value, CancellationToken.None);
        var all = await sut.GetClaimsAsync(ExistingPolicyId, CancellationToken.None);

        one.IsSuccess.Should().BeTrue();
        one.Value.ClaimId.Should().Be(declared.Value);
        one.Value.PolicyId.Should().Be(ExistingPolicyId,
            "a claim that loses its policy cannot be reconciled against the premium that funded it");

        all.IsSuccess.Should().BeTrue();
        all.Value.Select(c => c.ClaimId).Should().Contain(declared.Value);
    }

    [Fact]
    public async Task Adding_a_document_to_an_absent_claim_should_be_functional_not_found()
    {
        var result = await CreatePort().AddDocumentAsync(
            AbsentId, "kyc/2024/acte-deces.pdf", Key("absent-claim-doc"), CancellationToken.None);

        ShouldBeAbsentEntity(result);
    }

    [Fact]
    public async Task A_document_added_to_a_declared_claim_should_be_accepted()
    {
        var sut = CreatePort();

        var declared = await sut.DeclareAsync(Payload, Key("declare-for-doc"), CancellationToken.None);
        declared.IsSuccess.Should().BeTrue();

        var added = await sut.AddDocumentAsync(
            declared.Value, "kyc/2024/certificat-medical.pdf", Key("add-doc"), CancellationToken.None);

        // The insurer asks for the missing piece AFTER the declaration, so the claim reference it
        // returned has to still be addressable. One that is not leaves the file in
        // DocumentsRequired forever.
        added.IsSuccess.Should().BeTrue();
    }
}
