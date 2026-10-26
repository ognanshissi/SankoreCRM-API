namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Xunit;

/// <summary>Insurance lot 2: the institution as a distributor.</summary>
public sealed class InsuranceDistributorProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_broker_carries_the_art_540_mention()
    {
        var profile = InsuranceDistributorProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Now);

        profile.Update(InsuranceDistributorStatus.Broker, "ORIAS-1", null, null, Guid.NewGuid(), Now)
            .Error.Should().Be(InsuranceDistributionErrors.LegalMentionRequired);

        profile.Update(InsuranceDistributorStatus.Broker, "ORIAS-1", "Courtier agréé — art. 540", 60_000m, Guid.NewGuid(), Now)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_broker_needs_all_three_pieces_and_the_others_two()
    {
        InsuranceDistributorProfile.RequiredKinds(InsuranceDistributorStatus.Broker).Should().HaveCount(3);
        InsuranceDistributorProfile.RequiredKinds(InsuranceDistributorStatus.BancassurancePartner)
            .Should().BeEquivalentTo([DistributorDocumentKind.Approval, DistributorDocumentKind.ProfessionalLiability]);
    }

    [Fact]
    public void A_piece_is_valid_up_to_and_including_its_expiry_day()
    {
        var piece = DistributorDocument.Record(Guid.NewGuid(), DistributorDocumentKind.ProfessionalLiability,
            "RC-2026", "Assureur RC", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            null, null, null, Guid.NewGuid(), Now);

        piece.IsValidOn(new DateOnly(2026, 12, 31)).Should().BeTrue();
        piece.IsValidOn(new DateOnly(2027, 1, 1)).Should().BeFalse();
    }
}
