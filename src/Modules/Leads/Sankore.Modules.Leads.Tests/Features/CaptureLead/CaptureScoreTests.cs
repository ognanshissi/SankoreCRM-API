namespace Sankore.Modules.Leads.Tests.Features.CaptureLead;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.RecalculateLeadScore.Events;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// Lead temperature, lot 2: a lead is scored at its capture, before the event that dispatches it,
/// so it arrives « Froid » or « Tiède » instead of « Inconnu » — and its source's value counts from
/// the first second. A score only: the status stays the capture's, and no « score changed » event
/// is published for a first score.
/// </summary>
public sealed class CaptureScoreTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public CaptureScoreTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private CaptureLeadHandler Handler(Infrastructure.LeadsDbContext db)
    {
        var indexer = Substitute.For<IPhoneBlindIndexer>();
        indexer.Compute(Arg.Any<string>()).Returns(c => $"idx_{c.Arg<string>()}");
        return new CaptureLeadHandler(
            db, NullLogger<CaptureLeadHandler>.Instance, TimeProvider.System, Substitute.For<IBus>(), indexer,
            TestLeadEmailIndexer.Instance,
            Substitute.For<Sankore.Modules.Customers.PublicApi.ICustomersModule>(),
            Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>(),
            _publisher);
    }

    private CaptureLeadCommand Command(LeadSource source, Guid? leadSourceConfigId = null, string phone = "+2250709081102") => new(
        TenantId: _tenantId,
        FullName: "Mariam Coulibaly",
        PhoneNumber: phone,
        Source: source,
        InterestedProduct: "Crédit commerçant",
        PreferredLanguage: "fr",
        Latitude: 5.33,
        Longitude: -4.03,
        PreferredAgencyId: null,
        Force: true,
        LeadSourceConfigId: leadSourceConfigId);

    private async Task<Lead> CaptureAsync(CaptureLeadCommand cmd)
    {
        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(cmd, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
        await using var verify = _factory.CreateContext();
        return await verify.Leads.SingleAsync(l => l.Id == result.Value.LeadId);
    }

    private async Task<Guid> SourceAsync(int points)
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(_tenantId, $"S{points}", "Source", LeadChannelType.InboundWebhook, 0, scorePoints: points);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    [Fact]
    public async Task A_captured_lead_is_scored_and_its_temperature_derived_without_being_qualified()
    {
        var lead = await CaptureAsync(Command(LeadSource.Agency));

        lead.Score.Should().BeGreaterThan(0);
        lead.IntentLevel.Should().Be(Sankore.Modules.Leads.Features.Settings.IntentThresholds.IntentThresholds.Default.Of(lead.Score));
        lead.Status.Should().Be(LeadStatus.New, "scoring is not qualifying — that stays the agent's work");

        await using var db = _factory.CreateContext();
        var history = await db.ScoreHistories.SingleAsync(h => h.LeadId == lead.Id);
        history.TriggerEvent.Should().Be(CaptureScore.TriggerEvent);
        history.Score.Should().Be(lead.Score);
    }

    [Fact]
    public async Task A_first_score_is_not_announced_as_a_change()
    {
        await CaptureAsync(Command(LeadSource.Agency));

        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<LeadScoreCriticallyChangedIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_value_of_its_source_counts_from_the_capture()
    {
        var strong = await CaptureAsync(Command(LeadSource.Web, await SourceAsync(20), phone: "+2250709081103"));
        var weak = await CaptureAsync(Command(LeadSource.Web, await SourceAsync(4), phone: "+2250709081104"));

        (strong.Score - weak.Score).Should().Be(16);
    }
}
