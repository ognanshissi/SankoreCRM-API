namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using System.Text.Json;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Sources lot 1: a lead that came in through a configured source is worth, in the score's source
/// dimension, what the admin set on that source — else its channel's default. Before, every such
/// lead was scored as « Site web », 12 points.
/// </summary>
public sealed class LeadSourceScorePointsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public LeadSourceScorePointsTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SourceAsync(LeadChannelType channel, int? points)
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(_tenant, $"S{Guid.NewGuid():N}"[..10], "Source", channel, 0, scorePoints: points);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private Lead Capture(Guid? sourceId, Guid? customerId = null) => Lead.Capture(
        tenantId: _tenant,
        fullName: "Aya Kouassi",
        phoneNumber: "+2250700000001",
        source: LeadSource.Web,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "fr",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System,
        customerId: customerId,
        customerLinkIsAutomatic: customerId.HasValue,
        leadSourceConfigId: sourceId);

    private async Task<int> SourceScoreAsync(Lead lead)
    {
        await using var db = _factory.CreateContext();
        var (_, factorsJson) = await new LeadScoreCalculator(db).CalculateAsync(lead, CancellationToken.None);
        return JsonDocument.Parse(factorsJson).RootElement.GetProperty("sourceQuality").GetInt32();
    }

    [Fact]
    public async Task The_admins_value_is_what_the_lead_scores()
        => (await SourceScoreAsync(Capture(await SourceAsync(LeadChannelType.FacebookLeadAds, points: 3))))
            .Should().Be(3);

    [Fact]
    public async Task Without_a_value_the_channel_decides()
    {
        (await SourceScoreAsync(Capture(await SourceAsync(LeadChannelType.WalkIn, points: null)))).Should().Be(20);
        (await SourceScoreAsync(Capture(await SourceAsync(LeadChannelType.FacebookLeadAds, points: null)))).Should().Be(10);
        // Web forms keep the 12 points they scored as « Site web »: nothing moves for them.
        (await SourceScoreAsync(Capture(await SourceAsync(LeadChannelType.WebForm, points: null)))).Should().Be(12);
    }

    [Fact]
    public async Task A_source_that_no_longer_exists_falls_back_to_the_coarse_grid()
        => (await SourceScoreAsync(Capture(Guid.NewGuid()))).Should().Be(12);

    [Fact]
    public async Task An_existing_customer_still_takes_the_full_dimension()
        => (await SourceScoreAsync(Capture(await SourceAsync(LeadChannelType.FileImport, points: 2), Guid.NewGuid())))
            .Should().Be(20);

    [Fact]
    public void Every_channel_default_stays_inside_the_dimension()
    {
        foreach (var channel in Enum.GetValues<LeadChannelType>())
            LeadSourceConfig.DefaultScorePoints(channel).Should().BeInRange(0, LeadSourceConfig.MaxScorePoints, $"{channel}");
    }

    [Fact]
    public void A_value_outside_the_dimension_is_refused()
    {
        var act = () => LeadSourceConfig.Create(_tenant, "X", "X", LeadChannelType.WebForm, 0, scorePoints: 21);
        act.Should().Throw<DomainException>().WithMessage("LEAD_SOURCE_SCORE_POINTS_OUT_OF_RANGE");
    }

    [Fact]
    public void An_update_replaces_the_value_and_says_so()
    {
        var source = LeadSourceConfig.Create(_tenant, "X", "X", LeadChannelType.WebForm, 0, scorePoints: 15);

        var changed = source.Update(source.Label, source.Description, null, scorePoints: null);

        changed.Should().Contain(nameof(LeadSourceConfig.ScorePoints));
        source.ScorePoints.Should().BeNull();
        source.EffectiveScorePoints.Should().Be(12);
    }
}
