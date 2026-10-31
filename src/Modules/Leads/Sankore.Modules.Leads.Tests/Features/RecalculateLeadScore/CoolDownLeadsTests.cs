namespace Sankore.Modules.Leads.Tests.Features.RecalculateLeadScore;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Lead temperature, lot 4: a lead nobody touches cools down — the score loses points with
/// inactivity, and the night's job applies it to the untouched open leads, since a score only
/// moved when something happened to the lead.
/// </summary>
public sealed class CoolDownLeadsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public CoolDownLeadsTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private Lead Captured(int daysAgo) => Lead.Capture(
        tenantId: _tenant,
        fullName: "Aya Kouassi",
        phoneNumber: "+2250700000001",
        source: LeadSource.Agency,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "fr",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: new FixedClock(DateTimeOffset.UtcNow.AddDays(-daysAgo)));

    private async Task<int> InactivityAsync(Lead lead)
    {
        await using var db = _factory.CreateContext();
        var (_, factors) = await new LeadScoreCalculator(db).CalculateAsync(lead, CancellationToken.None);
        return JsonDocument.Parse(factors).RootElement.GetProperty("inactivity").GetInt32();
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(29, 0)]
    [InlineData(30, -10)]
    [InlineData(65, -20)]
    [InlineData(120, -30)]
    public async Task A_lead_untouched_since_its_capture_loses_points_with_the_months(int daysAgo, int penalty)
        => (await InactivityAsync(Captured(daysAgo))).Should().Be(penalty);

    [Fact]
    public async Task A_recent_activity_gives_the_points_back()
    {
        var lead = Captured(120);
        await using (var db = _factory.CreateContext())
        {
            db.LeadActivities.Add(LeadActivity.Create(_tenant, lead.Id, ActivityType.Call, "Appel", Guid.NewGuid(),
                performedAt: DateTimeOffset.UtcNow.AddDays(-2)));
            await db.SaveChangesAsync();
        }

        (await InactivityAsync(lead)).Should().Be(0);
    }

    [Fact]
    public async Task The_institution_can_switch_the_cooling_off()
    {
        await using (var db = _factory.CreateContext())
            (await new LeadSettingsService(db).SetAsync(_tenant, LeadSettingKeys.LeadCooldownEnabled, "false", Guid.NewGuid(), CancellationToken.None))
                .IsSuccess.Should().BeTrue();

        (await InactivityAsync(Captured(120))).Should().Be(0);
    }

    private async Task<Lead> SeedAsync(Lead lead, int score)
    {
        await using var db = _factory.CreateContext();
        lead.RecordScoreUpdate(score);
        lead.UpdateIntentLevel(LeadIntentLevel.Hot);
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private CoolDownLeadsJob Job()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        return new CoolDownLeadsJob(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CoolDownLeadsJob>.Instance);
    }

    [Fact]
    public async Task The_night_cools_a_forgotten_hot_lead_without_qualifying_it()
    {
        var forgotten = await SeedAsync(Captured(100), score: 85);

        (await Job().ExecuteAsync(_tenant)).Should().Be(1);

        await using var db = _factory.CreateContext();
        var lead = await db.Leads.SingleAsync(l => l.Id == forgotten.Id);
        lead.Score.Should().BeLessThan(85);
        lead.IntentLevel.Should().NotBe(LeadIntentLevel.Hot);
        lead.Status.Should().Be(LeadStatus.New);
        (await db.ScoreHistories.SingleAsync(h => h.LeadId == lead.Id)).TriggerEvent.Should().Be(CoolDownLeadsJob.TriggerEvent);
    }

    [Fact]
    public async Task Recently_captured_and_closed_leads_are_left_alone()
    {
        var recent = await SeedAsync(Captured(2), score: 85);
        var closed = Captured(100);
        closed.MarkLost("Pas intéressé");
        await SeedAsync(closed, score: 85);

        (await Job().ExecuteAsync(_tenant)).Should().Be(0);

        await using var db = _factory.CreateContext();
        (await db.Leads.SingleAsync(l => l.Id == recent.Id)).Score.Should().Be(85);
        (await db.Leads.SingleAsync(l => l.Id == closed.Id)).Score.Should().Be(85);
    }
}
