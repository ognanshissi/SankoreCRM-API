namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.Settings.IntentThresholds;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// « Un lead qui remplit un formulaire pour un produit donné est un lead chaud » (key accounts lot 3,
/// 2026-11-09, product owner): hot whatever its score while the request is fresh, through a source
/// whose option is on (the form channels by default), for an active catalogue product.
/// </summary>
public sealed class ProductRequestHeatTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public ProductRequestHeatTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        // The catalogue knows FOURNITURES; anything else resolves to no product.
        _admin.ResolveProductLineAsync(_tenantId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => c.ArgAt<string?>(1) == "FOURNITURES" ? Guid.NewGuid() : (Guid?)null);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<LeadSourceConfig> SourceAsync(LeadsDbContext db, LeadChannelType channel, bool? hot = null, string? defaultProduct = null)
    {
        var source = LeadSourceConfig.Create(_tenantId, $"S{Guid.NewGuid():N}"[..10], "Source", channel, 0,
            settings: channel == LeadChannelType.WebForm ? new EmbeddedScriptSettings { DefaultProductCode = defaultProduct } : null,
            productRequestIsHot: hot);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private Lead Lead(Guid sourceId, string product = "FOURNITURES")
        => LeadTestBuilder.Create().WithTenant(_tenantId).WithProduct(product).WithSourceConfig(sourceId).Build();

    private async Task<LeadIntentLevel> CapturedLevelAsync(LeadsDbContext db, Lead lead)
    {
        db.Leads.Add(lead);
        await CaptureScore.ApplyAsync(db, lead, default, _admin);
        await db.SaveChangesAsync();
        return lead.IntentLevel;
    }

    [Fact]
    public async Task A_web_form_asking_for_a_catalogue_product_is_hot_whatever_its_score()
    {
        await using var db = _factory.CreateContext();
        var source = await SourceAsync(db, LeadChannelType.WebForm);
        var lead = Lead(source.Id);

        (await CapturedLevelAsync(db, lead)).Should().Be(LeadIntentLevel.Hot);
        lead.Score.Should().BeLessThan(80, "only the temperature is raised, never the score");
        lead.ProductRequestAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_product_outside_the_catalogue_does_not_make_the_lead_hot()
    {
        await using var db = _factory.CreateContext();
        var source = await SourceAsync(db, LeadChannelType.WebForm);

        (await CapturedLevelAsync(db, Lead(source.Id, "Unknown"))).Should().NotBe(LeadIntentLevel.Hot);
        (await CapturedLevelAsync(db, Lead(source.Id, "Spatial"))).Should().NotBe(LeadIntentLevel.Hot);
    }

    [Theory]
    [InlineData(LeadChannelType.WebForm, null, true)]
    [InlineData(LeadChannelType.LinkedInLeadGen, null, true)]
    [InlineData(LeadChannelType.InboundWebhook, null, false)] // a webhook may relay a file: off by default
    [InlineData(LeadChannelType.InboundWebhook, true, true)]
    [InlineData(LeadChannelType.WebForm, false, false)]
    public async Task The_source_decides_with_the_channel_as_default(LeadChannelType channel, bool? option, bool hot)
    {
        await using var db = _factory.CreateContext();
        var source = await SourceAsync(db, channel, option);

        (await CapturedLevelAsync(db, Lead(source.Id)) == LeadIntentLevel.Hot).Should().Be(hot);
    }

    [Fact]
    public async Task A_contact_ending_not_interested_brings_the_score_back()
    {
        await using var db = _factory.CreateContext();
        var source = await SourceAsync(db, LeadChannelType.WebForm);
        var lead = Lead(source.Id);
        await CapturedLevelAsync(db, lead);

        db.LeadActivities.Add(LeadActivity.Create(_tenantId, lead.Id, ActivityType.Call, "Appel", Guid.NewGuid(),
            outcome: ActivityOutcome.NotInterested));
        await db.SaveChangesAsync();

        var calculator = new LeadScoreCalculator(db);
        (await calculator.IntentLevelOfAsync(lead, lead.Score, default)).Should().NotBe(LeadIntentLevel.Hot);
    }

    [Fact]
    public async Task A_request_nobody_answered_for_thirty_days_is_no_longer_hot()
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.MarkProductRequest(DateTimeOffset.UtcNow.AddDays(-31));

        (await ProductRequestHeat.IsActiveAsync(db, lead, DateTimeOffset.UtcNow, default)).Should().BeFalse();

        var fresh = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        fresh.MarkProductRequest(DateTimeOffset.UtcNow.AddDays(-29));
        (await ProductRequestHeat.IsActiveAsync(db, fresh, DateTimeOffset.UtcNow, default)).Should().BeTrue();
    }

    [Fact]
    public async Task New_thresholds_leave_a_fresh_request_hot()
    {
        Lead lead;
        await using (var db = _factory.CreateContext())
        {
            var source = await SourceAsync(db, LeadChannelType.WebForm);
            lead = Lead(source.Id);
            await CapturedLevelAsync(db, lead);
        }

        await using (var db = _factory.CreateContext())
        {
            var result = await new SetIntentThresholdsHandler(db, new LeadSettingsService(db))
                .Handle(new SetIntentThresholdsCommand(_tenantId, 90, 99, Guid.NewGuid()), default);
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        await using var verify = _factory.CreateContext();
        verify.Leads.Single(l => l.Id == lead.Id).IntentLevel.Should().Be(LeadIntentLevel.Hot);
    }

    [Fact]
    public void A_form_without_a_product_takes_the_source_default()
    {
        var source = LeadSourceConfig.Create(_tenantId, "DEVIS", "Devis", LeadChannelType.WebForm, 0,
            settings: new EmbeddedScriptSettings { DefaultProductCode = " FOURNITURES " });

        var lead = InboundLeadFactory.Build(_tenantId, source, new Dictionary<string, string?>(),
            "Awa Koné", "+2250700000001", "idx", null, TimeProvider.System);
        lead.InterestedProduct.Should().Be("FOURNITURES");

        var named = InboundLeadFactory.Build(_tenantId, source, new Dictionary<string, string?> { ["interestedProduct"] = "BUREAU" },
            "Awa Koné", "+2250700000001", "idx", null, TimeProvider.System);
        named.InterestedProduct.Should().Be("BUREAU", "a product the form sends wins over the default");
    }
}
