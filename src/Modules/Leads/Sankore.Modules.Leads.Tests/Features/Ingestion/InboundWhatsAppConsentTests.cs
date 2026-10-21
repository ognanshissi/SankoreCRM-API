namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Ingestion;
using Sankore.Modules.Leads.Features.LeadSources;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>The WhatsApp opt-in a form carries, recorded at capture (WA-BE-05).</summary>
public sealed class InboundWhatsAppConsentTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IPhoneBlindIndexer _indexer = Substitute.For<IPhoneBlindIndexer>();

    public InboundWhatsAppConsentTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _indexer.Compute(Arg.Any<string>()).Returns(ci => $"idx_{ci.Arg<string>()}");
    }

    public void Dispose() => _factory.Dispose();

    private async Task<LeadSourceConfig> SeedAsync(IntegrationMode mode, SourceSettings? settings = null)
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(_tenantId, "FORM", "Formulaire site", LeadChannelType.WebForm, 0, mode,
            settings: settings ?? new EmbeddedScriptSettings { Script = new ScriptConfig { AllowedOrigins = ["https://example.com"] } });
        source.StartTesting();
        source.Activate();
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<Guid?> IngestAsync(LeadSourceConfig source, string payload)
    {
        await using var db = _factory.CreateContext();
        var result = await new IngestInboundLeadHandler(db, _indexer, TimeProvider.System)
            .Handle(new IngestInboundLeadCommand(_tenantId, source.Id, payload), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value.LeadId;
    }

    [Fact]
    public async Task A_ticked_box_on_the_SDK_form_records_a_WhatsApp_consent_with_its_proof()
    {
        var source = await SeedAsync(IntegrationMode.EmbeddedScript);

        var leadId = await IngestAsync(source,
            """{"phoneNumber":"+2250707070707","fullName":"Awa Koné","_consentVersion":"v3","_whatsappConsent":"true"}""");

        await using var db = _factory.CreateContext();
        var consent = await db.LeadConsents.SingleAsync();
        consent.LeadId.Should().Be(leadId!.Value);
        consent.Type.Should().Be(ConsentType.WhatsAppContact);
        consent.Channel.Should().Be(ConsentChannel.WebForm);
        consent.Status.Should().Be(ConsentStatus.Active);
        consent.RecordedBy.Should().Be(Guid.Empty, "the prospect ticked the box, no agent recorded it");
        consent.ProofReference.Should().Contain("FORM").And.Contain("v3");
    }

    [Theory]
    [InlineData("""{"phoneNumber":"+2250707070707","fullName":"Awa Koné","_whatsappConsent":"false"}""")]
    [InlineData("""{"phoneNumber":"+2250707070707","fullName":"Awa Koné"}""")]
    public async Task An_unticked_box_or_no_box_records_nothing(string payload)
    {
        var source = await SeedAsync(IntegrationMode.EmbeddedScript);

        await IngestAsync(source, payload);

        await using var db = _factory.CreateContext();
        (await db.LeadConsents.CountAsync()).Should().Be(0, "an optional box left empty is not a refusal the prospect stated");
    }

    [Fact]
    public async Task A_webhook_source_reads_the_opt_in_where_its_settings_say()
    {
        var source = await SeedAsync(
            IntegrationMode.ServerWebhook,
            new ServerWebhookSettings { Consent = new ConsentConfig { WhatsAppConsentFieldPath = "$.optin_whatsapp" } });

        await IngestAsync(source, """{"phoneNumber":"+2250707070707","fullName":"Awa Koné","optin_whatsapp":"oui"}""");

        await using var db = _factory.CreateContext();
        (await db.LeadConsents.SingleAsync()).Type.Should().Be(ConsentType.WhatsAppContact);
    }

    [Fact]
    public async Task A_second_submission_of_the_same_person_brings_the_opt_in_to_the_lead_they_already_are()
    {
        var source = await SeedAsync(IntegrationMode.EmbeddedScript);

        var first = await IngestAsync(source, """{"phoneNumber":"+2250707070707","fullName":"Awa Koné"}""");
        var second = await IngestAsync(source,
            """{"phoneNumber":"+2250707070707","fullName":"Awa Koné","_whatsappConsent":"true"}""");

        second.Should().Be(first, "the dedup window files the second form on the same lead");

        await using var db = _factory.CreateContext();
        (await db.LeadConsents.SingleAsync()).LeadId.Should().Be(first!.Value);
    }

    [Theory]
    [InlineData("$.consents.whatsapp", true)]
    [InlineData("optin", true)]
    [InlineData("$.[[[", false)]
    public void A_configured_path_must_be_readable(string path, bool valid)
        => WhatsAppConsentSettingsRules.IsReadablePath(path).Should().Be(valid);
}
