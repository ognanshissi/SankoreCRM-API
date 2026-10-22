namespace Sankore.Modules.Leads.Tests.Features.WhatsApp;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel.Realtime;
using Xunit;

public sealed class WhatsAppTests : IDisposable
{
    private const string Key = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly TestDbContextFactory _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly IMessagingGateway _messaging = Substitute.For<IMessagingGateway>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ILeadSettings _settings = Substitute.For<ILeadSettings>();
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();
    private readonly IBlindIndexer _blindIndexer =
        new HmacBlindIndexer(Options.Create(new FieldProtectionOptions { SectionName = "Leads", FieldEncryptionKey = Key, BlindIndexKey = Key }));
    private readonly IPhoneBlindIndexer _phoneIndexer = new HmacPhoneBlindIndexer(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Leads:BlindIndexKey"] = Key }).Build());

    public WhatsAppTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenantId);
        // A bypass role: these tests are about WhatsApp's rules, not about who owns the lead.
        _currentUser.Roles.Returns(["Administrator"]);

        _integration.Messaging.Returns(_messaging);
        _messaging.ResolveChannelAsync(_tenantId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new MessagingChannelInfo(_connectionId, "Numéro principal", "Dialog360", "+2250700000000", null, true));

        _admin.GetSmsConfigAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto("Disabled", null, null, "225", false, false, null, 0));

        _customers.FindMatchesAsync(default, default, default, default, default)
            .ReturnsForAnyArgs([]);

        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppCreateLeadForUnknownNumber, Arg.Any<CancellationToken>())
            .Returns(true);
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── Numbers ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("+225 07 08 09 18 01", null, "+2250708091801")]
    [InlineData("002250708091801", null, "+2250708091801")]
    [InlineData("0708091801", "225", "+2250708091801")]
    [InlineData("07-08-09-18-01", "+225", "+2250708091801")]
    public void A_number_is_written_in_E164(string raw, string? code, string expected)
        => WhatsAppNumbers.ToE164(raw, code).Should().Be(expected);

    [Theory]
    [InlineData("0708091801", null)]  // local, and no calling code to complete it
    [InlineData("+12", null)]         // too short
    [InlineData("07 08 ab 18", "225")] // not a number
    public void A_number_that_cannot_be_written_in_E164_is_refused(string raw, string? code)
        => WhatsAppNumbers.ToE164(raw, code).Should().BeNull();

    // ── Domain ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_status_never_steps_back()
    {
        var message = Template();
        message.MarkSent("wamid.1", _now);

        message.ApplyStatus("read", _now.AddMinutes(2), null, null).Should().BeTrue();
        message.ApplyStatus("delivered", _now.AddMinutes(1), null, null).Should().BeFalse();
        message.ApplyStatus("failed", _now.AddMinutes(3), "MESSAGING_NOT_DELIVERED", null).Should().BeFalse();

        message.Status.Should().Be(WhatsAppMessageStatus.Read);
        message.DeliveredAt.Should().Be(_now.AddMinutes(2), "a read message was delivered, even if that status was lost");
    }

    [Fact]
    public void A_message_is_taken_for_sending_once_and_its_values_go_once_sent()
    {
        var message = Template();

        message.TryBeginSending().Should().BeTrue();
        message.TryBeginSending().Should().BeFalse("a redelivered event must not send it again");

        message.MarkSent("wamid.1", _now);
        message.TemplateParameters.Should().BeNull();
    }

    [Fact]
    public void The_window_opens_for_24_hours_from_the_customer_s_message_and_lifts_the_cooldown()
    {
        var conversation = Conversation(null);
        conversation.RecordTemplateSent(_now);

        conversation.NextTemplateAllowedAt(_now.AddHours(1)).Should().Be(_now.AddHours(24));
        conversation.IsWindowOpen(_now.AddHours(1)).Should().BeFalse();

        conversation.RecordInbound(_now.AddHours(2), "Awa");

        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.WindowExpiresAt.Should().Be(_now.AddHours(26));
        conversation.NextTemplateAllowedAt(_now.AddHours(3)).Should().BeNull("the customer answered");
        conversation.UnreadCount.Should().Be(1);
    }

    [Fact]
    public void A_template_counts_and_fills_its_variables_and_keeps_its_mapping_across_a_sync()
    {
        var template = WhatsAppTemplate.Discover(_tenantId, _connectionId, "premier_contact", "fr", _now);
        template.Refresh("utility", "APPROVED", null, null, "Bonjour {{1}}, je suis {{2}}.", null, [], _now);
        template.Configure([], [new WhatsAppVariableMapping(1, WhatsAppTemplateField.LeadFirstName)], _me, _now)
            .IsSuccess.Should().BeTrue();

        template.VariableCount.Should().Be(2);
        template.Render(["Awa", "Koffi"]).Should().Be("Bonjour Awa, je suis Koffi.");

        template.Refresh("UTILITY", "APPROVED", null, null, "Bonjour {{1}}.", null, [], _now.AddHours(1));

        template.VariableMappings.Should().ContainSingle(m => m.Index == 1);
        template.IsSendable.Should().BeTrue();
    }

    [Fact]
    public void A_mapping_naming_a_variable_the_template_does_not_have_is_refused()
    {
        var template = WhatsAppTemplate.Discover(_tenantId, _connectionId, "relance", "fr", _now);
        template.Refresh("UTILITY", "APPROVED", null, null, "Bonjour {{1}}.", null, [], _now);

        template.Configure([], [new WhatsAppVariableMapping(2, WhatsAppTemplateField.LeadFullName)], _me, _now)
            .Error.Should().Be("WHATSAPP_TEMPLATE_VARIABLE_UNKNOWN");
    }

    // ── Sending ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_consent_nothing_is_queued()
    {
        var (lead, template) = await SeedAsync(consent: false);

        var result = await Send(lead.Id, template.Id, ["Awa"]);

        result.Error.Should().Be(WhatsAppErrors.ConsentMissing);
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync<WhatsAppMessageQueuedEvent>(default!, default);
    }

    [Fact]
    public async Task A_template_is_queued_with_its_rendered_body_and_the_conversation_awaits_a_reply()
    {
        var (lead, template) = await SeedAsync();

        var result = await Send(lead.Id, template.Id, ["Awa"]);

        result.IsSuccess.Should().BeTrue();

        await using var read = _db.CreateContext();
        var message = await read.WhatsAppMessages.SingleAsync();
        var conversation = await read.WhatsAppConversations.SingleAsync();

        message.Status.Should().Be(WhatsAppMessageStatus.Queued);
        message.Body.Should().Be("Bonjour Awa, nous avons bien reçu votre demande.");
        message.AuthorUserId.Should().Be(_me);
        conversation.LeadId.Should().Be(lead.Id);
        conversation.ContactPhone.Should().Be("+221771234567");
        conversation.Status.Should().Be(WhatsAppConversationStatus.AwaitingReply);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<WhatsAppMessageQueuedEvent>(e => e.MessageId == message.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_template_within_24_hours_without_an_answer_needs_the_force_permission()
    {
        var (lead, template) = await SeedAsync();

        (await Send(lead.Id, template.Id, ["Awa"])).IsSuccess.Should().BeTrue();

        (await Send(lead.Id, template.Id, ["Awa"])).Error.Should().Be(WhatsAppErrors.TemplateCooldown);
        (await Send(lead.Id, template.Id, ["Awa"], mayForce: true)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task One_value_per_variable_and_no_line_break()
    {
        var (lead, template) = await SeedAsync();

        (await Send(lead.Id, template.Id, [])).Error.Should().Be(WhatsAppErrors.VariablesInvalid);
        (await Send(lead.Id, template.Id, ["Awa\nKoné"])).Error.Should().Be(WhatsAppErrors.VariablesInvalid);
    }

    [Fact]
    public async Task A_template_not_approved_is_never_sent()
    {
        var (lead, template) = await SeedAsync(templateStatus: "PENDING");

        (await Send(lead.Id, template.Id, ["Awa"])).Error.Should().Be(WhatsAppErrors.TemplateNotApproved);
    }

    // ── Receiving ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_lands_on_the_lead_by_its_number_once_and_establishes_contact()
    {
        var (lead, _) = await SeedAsync(consent: false);
        var inbound = Inbound("wamid.in.1", "+221771234567", "Oui, rappelez-moi demain");

        var first = await Record(new MessagingEventBatch([inbound], []));
        var replay = await Record(new MessagingEventBatch([inbound], []));

        first.MessagesRecorded.Should().Be(1);
        replay.MessagesDuplicate.Should().Be(1, "the provider's message id makes the recording idempotent");

        await using var read = _db.CreateContext();
        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.LeadId.Should().Be(lead.Id);
        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);

        (await read.WhatsAppMessages.SingleAsync()).Body.Should().Be("Oui, rappelez-moi demain");
        (await read.Leads.SingleAsync()).Status.Should().Be(LeadStatus.Open);

        var consent = await read.LeadConsents.SingleAsync();
        consent.Type.Should().Be(ConsentType.WhatsAppContact);
        consent.Channel.Should().Be(ConsentChannel.WhatsApp);
    }

    [Fact]
    public async Task An_unknown_number_becomes_a_WhatsApp_lead()
    {
        var outcome = await Record(new MessagingEventBatch([Inbound("wamid.in.2", "+2250501020304", "Bonjour", "Kouamé")], []));

        outcome.MessagesRecorded.Should().Be(1);

        await using var read = _db.CreateContext();
        var lead = await read.Leads.SingleAsync();
        lead.Source.Should().Be(LeadSource.WhatsApp);
        lead.FullName.Should().Be("Kouamé");
        (await read.WhatsAppConversations.SingleAsync()).LeadId.Should().Be(lead.Id);
    }

    [Fact]
    public async Task A_withdrawn_consent_is_not_given_back_by_a_message()
    {
        var (lead, _) = await SeedAsync();

        await using (var write = _db.CreateContext())
        {
            var consent = await write.LeadConsents.AsTracking().SingleAsync();
            consent.Withdraw(_me, TimeProvider.System, "STOP").IsSuccess.Should().BeTrue();
            await write.SaveChangesAsync();
        }

        await Record(new MessagingEventBatch([Inbound("wamid.in.3", "+221771234567", "Encore moi")], []));

        await using var read = _db.CreateContext();
        (await read.LeadConsents.SingleAsync()).Status.Should().Be(ConsentStatus.Withdrawn);
    }

    [Fact]
    public async Task A_status_for_an_unknown_message_is_reported_unmatched()
    {
        var outcome = await Record(new MessagingEventBatch([], [new MessagingDeliveryStatus("wamid.nowhere", "delivered", _now, null, null)]));

        outcome.StatusesUnmatched.Should().Be(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private WhatsAppMessage Template()
        => WhatsAppMessage.OutboundTemplate(_tenantId, Guid.NewGuid(), _me, Guid.NewGuid(), "premier_contact", "fr", "Bonjour Awa", "[\"Awa\"]", _now);

    private WhatsAppConversation Conversation(Guid? leadId)
        => WhatsAppConversation.Open(_tenantId, _connectionId, "+221771234567", "idx", leadId, null, null, false, _now);

    private static InboundMessagingMessage Inbound(string id, string from, string text, string? profile = null)
        => new(id, from, profile, new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.Zero), "text", text, null, null, null, null);

    private async Task<(Lead Lead, WhatsAppTemplate Template)> SeedAsync(bool consent = true, string templateStatus = "APPROVED")
    {
        await using var db = _db.CreateContext();

        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Fresh().Build();
        db.Leads.Add(lead);

        if (consent)
        {
            db.LeadConsents.Add(LeadConsent.Create(
                _tenantId, lead.Id, ConsentType.WhatsAppContact, ConsentChannel.WebForm, _me, TimeProvider.System, "Formulaire web"));
        }

        var template = WhatsAppTemplate.Discover(_tenantId, _connectionId, "premier_contact", "fr", _now);
        template.Refresh("UTILITY", templateStatus, null, null, "Bonjour {{1}}, nous avons bien reçu votre demande.", null, [], _now);
        db.WhatsAppTemplates.Add(template);

        await db.SaveChangesAsync();
        return (lead, template);
    }

    private async Task<Sankore.Shared.Kernel.Result<SendWhatsAppTemplateResult>> Send(
        Guid leadId, Guid templateId, IReadOnlyList<string> values, bool mayForce = false)
    {
        await using var db = _db.CreateContext();

        var handler = new SendWhatsAppTemplateHandler(
            db, _currentUser, new LeadAccessGuard(_currentUser, _admin), _admin, _integration,
            _blindIndexer, _publisher, new FixedClock(_now));

        return await handler.Handle(new SendWhatsAppTemplateCommand(leadId, templateId, values, mayForce), CancellationToken.None);
    }

    /// <summary>Lot 2's escalation, over the test context — the handler's own dependency.</summary>
    private WhatsAppEscalation Escalation(LeadsDbContext db) =>
        new(db, _customers, _settings, _publisher, NullLogger<WhatsAppEscalation>.Instance);

    /// <summary>No model on this deployment, so no bot: an inbound message escalates to a person.</summary>
    private WhatsAppBotProfiles BotProfiles(LeadsDbContext db) =>
        new(db, _settings, new UnavailableWhatsAppBotModel());

    private async Task<MessagingEventsOutcome> Record(MessagingEventBatch batch)
    {
        await using var db = _db.CreateContext();

        var handler = new RecordMessagingEventsHandler(
            db, _phoneIndexer, _blindIndexer, _customers, _settings, _realtime, _publisher,
            Escalation(db), BotProfiles(db),
            WhatsAppTestServices.Prospects(db, _phoneIndexer, _publisher, WhatsAppTestServices.FieldValues(_admin)),
            WhatsAppTestServices.FieldValues(_admin),
            new WhatsAppAudit(Substitute.For<IAuditWriter>(), TimeProvider.System, NullLogger<WhatsAppAudit>.Instance),
            new FixedClock(_now), NullLogger<RecordMessagingEventsHandler>.Instance);

        return await handler.Handle(
            new RecordMessagingEventsCommand(_tenantId, _connectionId, null, batch), CancellationToken.None);
    }
}
