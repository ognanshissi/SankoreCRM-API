namespace Sankore.Modules.Leads.Tests.Features.Settings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.Settings.IntentThresholds;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// Lead temperature, lot 3: the scores from which a lead reads warm and hot are the institution's,
/// below warm a scored lead is cold (no longer « Inconnu »), and changing them re-reads the open
/// leads' temperature from their stored score.
/// </summary>
public sealed class IntentThresholdsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public IntentThresholdsTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(85, LeadIntentLevel.Hot)]
    [InlineData(80, LeadIntentLevel.Hot)]
    [InlineData(60, LeadIntentLevel.Warm)]
    [InlineData(59, LeadIntentLevel.Cold)]
    [InlineData(12, LeadIntentLevel.Cold)]
    public void The_default_bands(int score, LeadIntentLevel level)
        => IntentThresholds.Default.Of(score).Should().Be(level);

    [Theory]
    [InlineData(0, 80)]
    [InlineData(60, 60)]
    [InlineData(70, 60)]
    [InlineData(50, 101)]
    public void A_pair_out_of_order_or_range_is_not_valid(int warm, int hot)
        => new IntentThresholds(warm, hot).IsValid.Should().BeFalse();

    private async Task<Lead> SeedAsync(int score, bool closed = false)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        lead.RecordScoreUpdate(score);
        lead.UpdateIntentLevel(IntentThresholds.Default.Of(score));
        if (closed) lead.MarkLost("Pas intéressé");
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<Dictionary<Guid, LeadIntentLevel>> LevelsAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.ToDictionaryAsync(l => l.Id, l => l.IntentLevel);
    }

    private async Task<Sankore.Shared.Kernel.Result<int>> SetAsync(int warm, int hot)
    {
        await using var db = _factory.CreateContext();
        return await new SetIntentThresholdsHandler(db, new LeadSettingsService(db))
            .Handle(new SetIntentThresholdsCommand(_tenant, warm, hot, Guid.NewGuid()), CancellationToken.None);
    }

    [Fact]
    public async Task New_thresholds_re_read_the_open_scored_leads_and_leave_the_others()
    {
        var fifty = await SeedAsync(50);
        var seventy = await SeedAsync(70);
        var closed = await SeedAsync(50, closed: true);
        var unscored = await SeedAsync(0);
        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking().SingleAsync(l => l.Id == unscored.Id);
            tracked.UpdateIntentLevel(LeadIntentLevel.Unknown);
            await db.SaveChangesAsync();
        }

        var result = await SetAsync(40, 70);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().Be(2);
        var levels = await LevelsAsync();
        levels[fifty.Id].Should().Be(LeadIntentLevel.Warm);
        levels[seventy.Id].Should().Be(LeadIntentLevel.Hot);
        levels[closed.Id].Should().Be(LeadIntentLevel.Cold, "a closed lead keeps the temperature it closed with");
        levels[unscored.Id].Should().Be(LeadIntentLevel.Unknown, "nobody scored it");
    }

    [Fact]
    public async Task A_pair_out_of_order_is_refused_and_nothing_is_written()
    {
        (await SetAsync(70, 70)).Error.Should().Be(SetIntentThresholdsHandler.OutOfOrder);

        await using var db = _factory.CreateContext();
        (await IntentThresholds.ReadAsync(db, _tenant, CancellationToken.None)).Should().Be(IntentThresholds.Default);
    }

    [Fact]
    public async Task The_scorer_reads_the_tenants_thresholds()
    {
        (await SetAsync(20, 30)).IsSuccess.Should().BeTrue();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();

        await using var db = _factory.CreateContext();
        (await new LeadScoreCalculator(db).IntentLevelOfAsync(lead, 35, CancellationToken.None)).Should().Be(LeadIntentLevel.Hot);
    }
}
