namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The 60-point threshold was calibrated for qualification AFTER interactions: of the 100 points
/// LeadScoreCalculator can award, 35 come from activity history (20 interactions + 15 behaviour),
/// which is empty at capture. A lead scored on capture alone therefore peaks at 60 for a walk-in
/// and at 45 for a file import — so auto-qualification with the historical threshold would
/// qualify nothing at all. Hence the parameter.
/// </summary>
public sealed class QualificationThresholdTests
{
    private static Lead Fresh() => Lead.Capture(
        tenantId: Guid.NewGuid(),
        fullName: "Awa Ouattara",
        phoneNumber: "+2250708091801",
        source: LeadSource.FileImport,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "FR",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System);

    [Fact]
    public void The_default_threshold_is_unchanged_at_60()
    {
        Lead.DefaultQualifiedThreshold.Should().Be(60);

        var lead = Fresh();
        lead.Qualify(59);
        lead.Status.Should().Be(LeadStatus.Qualifying);

        var other = Fresh();
        other.Qualify(60);
        other.Status.Should().Be(LeadStatus.Qualified);
    }

    [Theory]
    [InlineData(30, 30, LeadStatus.Qualified)]
    [InlineData(30, 29, LeadStatus.Qualifying)]
    [InlineData(80, 79, LeadStatus.Qualifying)]
    [InlineData(80, 80, LeadStatus.Qualified)]
    public void A_configured_threshold_decides_the_status(
        int threshold, int score, LeadStatus expected)
    {
        var lead = Fresh();

        lead.Qualify(score, threshold);

        lead.Status.Should().Be(expected);
    }

    [Fact]
    public void A_score_of_30_qualifies_an_imported_lead_only_when_the_threshold_is_lowered()
    {
        // 30 is what a complete row of docs/sample-lead-import.xlsx actually scores.
        var withDefault = Fresh();
        withDefault.Qualify(30);
        withDefault.Status.Should().Be(LeadStatus.Qualifying, "45 is the ceiling for a file import");

        var withLowered = Fresh();
        withLowered.Qualify(30, qualifiedThreshold: 30);
        withLowered.Status.Should().Be(LeadStatus.Qualified);
    }

    [Fact]
    public void Qualifying_never_revives_a_closed_lead()
    {
        var lead = Fresh();
        lead.Close(LeadCloseReason.Lost);

        var result = lead.Qualify(100, qualifiedThreshold: 1);

        result.IsFailure.Should().BeTrue();
        lead.Status.Should().Be(LeadStatus.Lost);
    }
}
