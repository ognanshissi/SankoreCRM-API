namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Xunit;

/// <summary>
/// KRM-B-02: the file remembers which product the customer was converted on and which agent is
/// accountable for it.
///
/// <para>
/// The interesting rule is the refusal. <c>ClientCreatedKycConsumer</c> and
/// <c>KycRequestedConsumer</c> both fire for one conversion and M01's event usually wins, so the
/// context is normally applied to a file that ALREADY EXISTS — which is why it is applied on that
/// path at all. The same path is also walked by a customer subscribing a SECOND product months
/// later, and there it must do nothing: re-pointing a validated file at the new line would make the
/// evidence appear to have been collected for a product nobody validated it for.
/// </para>
/// </summary>
public sealed class ConversionContextTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();

    private static KycFile Open(Guid? productLineId = null, Guid? agentId = null)
        => KycFile.Open(
            Tenant, Customer, KycChannel.LeadConversion, agentId ?? Guid.NewGuid(),
            TimeProvider.System,
            productLineId: productLineId,
            responsibleAgentId: agentId);

    [Fact]
    public void A_file_opened_by_a_conversion_carries_the_product_and_the_agent()
    {
        var product = Guid.NewGuid();
        var agent = Guid.NewGuid();

        var file = Open(product, agent);

        file.ProductLineId.Should().Be(product);
        file.ResponsibleAgentId.Should().Be(agent);
    }

    [Fact]
    public void Guid_Empty_is_not_a_product_and_not_an_agent()
    {
        // A caller passing it means "unknown". Storing it would create a reference nothing
        // resolves while looking like a real value — the same rule AgencyId follows.
        var file = KycFile.Open(
            Tenant, Customer, KycChannel.Agency, Guid.NewGuid(), TimeProvider.System,
            productLineId: Guid.Empty, responsibleAgentId: Guid.Empty);

        file.ProductLineId.Should().BeNull();
        file.ResponsibleAgentId.Should().BeNull();
    }

    [Fact]
    public void A_counter_file_carries_neither_and_that_is_not_degraded()
    {
        var file = KycFile.Open(
            Tenant, Customer, KycChannel.Agency, Guid.NewGuid(), TimeProvider.System);

        file.ProductLineId.Should().BeNull("a file opened at a counter is about a customer, not a product");
    }

    [Fact]
    public void The_context_is_attached_to_a_file_that_already_exists()
    {
        // THE case this method exists for: M01's event opened the file first and knows no product.
        var file = Open();
        var product = Guid.NewGuid();
        var agent = Guid.NewGuid();

        file.AttachConversionContext(product, agent, TimeProvider.System).Should().BeTrue();

        file.ProductLineId.Should().Be(product);
        file.ResponsibleAgentId.Should().Be(agent);
    }

    [Fact]
    public void Attaching_twice_does_not_move_what_is_already_there()
    {
        // Whichever conversion opened this round of collection owns it. A second one arriving must
        // not re-point a file somebody is already working.
        var first = Guid.NewGuid();
        var file = Open(first, Guid.NewGuid());

        file.AttachConversionContext(Guid.NewGuid(), Guid.NewGuid(), TimeProvider.System)
            .Should().BeFalse();

        file.ProductLineId.Should().Be(first);
    }

    [Fact]
    public void A_null_argument_leaves_that_field_alone()
    {
        // M01's trigger knows an agent and no product; passing null for the product must not erase
        // one that M13's trigger already supplied.
        var product = Guid.NewGuid();
        var file = Open(product);

        var agent = Guid.NewGuid();
        file.AttachConversionContext(null, agent, TimeProvider.System).Should().BeTrue();

        file.ProductLineId.Should().Be(product);
        file.ResponsibleAgentId.Should().Be(agent);
    }

    [Fact]
    public void An_already_approved_file_refuses_the_context_of_a_new_product()
    {
        // A customer with a validated KYC subscribing a second product. The file must keep saying
        // what it was validated for.
        var original = Guid.NewGuid();
        var file = Open(original, Guid.NewGuid());

        file.SubmitForVerification(Guid.NewGuid(), TimeProvider.System);
        file.RecordVerification(92, KycConfidenceLevel.High, TimeProvider.System);
        file.Approve(KycTier.Simplified, Guid.NewGuid(), TimeProvider.System);

        file.AttachConversionContext(Guid.NewGuid(), Guid.NewGuid(), TimeProvider.System)
            .Should().BeFalse();

        file.ProductLineId.Should().Be(original);
    }

    [Fact]
    public void A_file_still_collecting_accepts_it_even_after_a_complement()
    {
        // Still Tier.None: nothing has been granted, so nothing is being rewritten.
        var file = Open();

        file.SubmitForVerification(Guid.NewGuid(), TimeProvider.System);
        file.RequestComplement(TimeProvider.System);

        file.AttachConversionContext(Guid.NewGuid(), Guid.NewGuid(), TimeProvider.System)
            .Should().BeTrue();
    }

    [Fact]
    public void The_responsible_agent_is_not_a_four_eyes_anchor()
    {
        // It is an ADDRESS — who gets told, who gets chased. LastSubmittedBy and
        // ManuallyValidatedBy are the anchors, and a remotely submitted file deliberately carries
        // SYSTEM in the first, which is what leaves this agent free to decide.
        var agent = Guid.NewGuid();
        var file = Open(Guid.NewGuid(), agent);

        file.SubmitForVerification(Guid.Empty, TimeProvider.System);
        file.RecordVerification(90, KycConfidenceLevel.High, TimeProvider.System);

        file.Approve(KycTier.Simplified, agent, TimeProvider.System)
            .IsSuccess.Should().BeTrue(
                "the responsible agent produced no evidence, so nothing bars them from deciding");
    }
}
