namespace Sankore.Modules.Leads.Tests.Features.WhatsApp;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Realtime;
using Xunit;

/// <summary>Lot 2: escalation, STOP, the one-holder rule, free replies, the takeover SLA, the inbox.</summary>
public sealed class WhatsAppTakeoverTests : IDisposable
{
    private const string Key = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly TestDbContextFactory _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ILeadSettings _settings = Substitute.For<ILeadSettings>();
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();
    private readonly IBlindIndexer _blindIndexer =
        new HmacBlindIndexer(Options.Create(new FieldProtectionOptions { SectionName = "Leads", FieldEncryptionKey = Key, BlindIndexKey = Key }));
    private readonly IPhoneBlindIndexer _phoneIndexer = new HmacPhoneBlindIndexer(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Leads:BlindIndexKey"] = Key }).Build());

    public WhatsAppTakeoverTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Roles.Returns(["Agent"]);

        _customers.FindMatchesAsync(default, default, default, default, default).ReturnsForAnyArgs([]);
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.WhatsAppTakeoverSlaMinutes, Arg.Any<CancellationToken>()).Returns(30);
        _settings.GetStringAsync(_tenantId, LeadSettingKeys.WhatsAppWaitingMessage, Arg.Any<CancellationToken>()).Returns("Un conseiller arrive.");
        _settings.GetStringAsync(_tenantId, LeadSettingKeys.WhatsAppOptOutConfirmation, Arg.Any<CancellationToken>()).Returns("C'est noté.");
        _admin.GetTeamAgentIdsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([_colleague]);
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ── STOP ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("STOP", true)]
    [InlineData("  stop ", true)]
    [InlineData("Arrêt !", true)]
    [InlineData("arreter", true)]
    [InlineData("Ne stoppez pas mon dossier", false)]
    [InlineData("stop stop", false)]
    [InlineData("Bonjour", false)]
    public void Only_the_whole_message_is_an_opt_out(string text, bool stop)
        => WhatsAppOptOut.IsStop(text).Should().Be(stop);

    [Fact]
    public async Task STOP_withdraws_the_consent_closes_the_conversation_and_answers_without_escalating()
    {
        var lead = await SeedLeadAsync(consent: true, ownerId: _colleague);

        await RecordAsync(Inbound("wamid.stop", "STOP"));

        await using var read = _db.CreateContext();
        (await read.LeadConsents.SingleAsync()).Status.Should().Be(ConsentStatus.Withdrawn);

        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.Status.Should().Be(WhatsAppConversationStatus.Closed);
        conversation.CloseReason.Should().Be("OPT_OUT");

        var confirmation = await read.WhatsAppMessages.SingleAsync(m => m.Direction == WhatsAppDirection.Outbound);
        confirmation.Author.Should().Be(WhatsAppAuthor.System);
        confirmation.Body.Should().Be("C'est noté.");

        await _publisher.DidNotReceive().PublishAsync(Arg.Any<WhatsAppConversationEscalatedEvent>(), Arg.Any<CancellationToken>());
        _ = lead;
    }

    // ── Escalation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_waits_for_the_lead_s_agent_starts_the_SLA_and_tells_the_customer_once()
    {
        await SeedLeadAsync(consent: true, ownerId: _colleague);

        await RecordAsync(Inbound("wamid.1", "Bonjour"));
        await RecordAsync(Inbound("wamid.2", "Vous êtes là ?", minutes: 1));

        await using var read = _db.CreateContext();
        var conversation = await read.WhatsAppConversations.SingleAsync();

        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.TargetAgentId.Should().Be(_colleague);
        conversation.TakeoverDeadline.Should().Be(_now.AddMinutes(30));
        conversation.EscalationReason.Should().Be("CUSTOMER_REPLIED");

        (await read.WhatsAppMessages.CountAsync(m => m.Author == WhatsAppAuthor.System))
            .Should().Be(1, "the second message is already waiting: the customer is told once");

        await _publisher.Received(1).PublishAsync(Arg.Any<WhatsAppConversationEscalatedEvent>(), Arg.Any<CancellationToken>());
    }

    // ── Holding ────────────────────────────────────────────────────────────────

    [Fact]
    public void One_holder_at_a_time_and_only_a_reassignment_moves_it()
    {
        var conversation = Conversation();
        conversation.RecordInbound(_now, null);
        conversation.Escalate("CUSTOMER_REPLIED", null, _me, TimeSpan.FromMinutes(30), _now);

        conversation.TakeOver(_me, _now).IsSuccess.Should().BeTrue();
        conversation.TakeoverDeadline.Should().BeNull("taking over stops the SLA");

        conversation.TakeOver(_colleague, _now).Error.Should().Be("WHATSAPP_CONVERSATION_HELD_BY_ANOTHER");

        conversation.Reassign(_colleague, TimeSpan.FromMinutes(30), _now).IsSuccess.Should().BeTrue();
        conversation.OwnerUserId.Should().Be(_colleague);
    }

    [Fact]
    public async Task A_free_reply_needs_the_holder_an_open_window_and_no_opt_out()
    {
        var lead = await SeedLeadAsync(consent: true, ownerId: _me);
        var conversationId = await SeedConversationAsync(lead.Id, holder: _colleague, windowOpen: true);

        (await ReplyAsync(lead.Id, conversationId, "Bonjour")).Error.Should().Be("WHATSAPP_NOT_HOLDER");

        var mine = await SeedConversationAsync(lead.Id, holder: _me, windowOpen: false, connection: Guid.NewGuid());
        (await ReplyAsync(lead.Id, mine, "Bonjour")).Error.Should().Be("WHATSAPP_WINDOW_CLOSED");

        var open = await SeedConversationAsync(lead.Id, holder: _me, windowOpen: true, connection: Guid.NewGuid());
        var sent = await ReplyAsync(lead.Id, open, "Bonjour, je vous écoute.");
        sent.IsSuccess.Should().BeTrue();

        await using var read = _db.CreateContext();
        var message = await read.WhatsAppMessages.SingleAsync(m => m.Id == sent.Value);
        message.Status.Should().Be(WhatsAppMessageStatus.Queued);
        message.AuthorUserId.Should().Be(_me);
        await _publisher.Received().PublishAsync(Arg.Is<WhatsAppMessageQueuedEvent>(e => e.MessageId == sent.Value), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 }, "image/jpeg", true)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "application/pdf", true)]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03 }, "application/pdf", false)]
    public void A_file_must_be_what_its_type_says(byte[] content, string type, bool accepted)
        => SendWhatsAppMediaHandler.MatchesSignature(content, type).Should().Be(accepted);

    // ── Takeover SLA ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_breached_SLA_redispatches_the_lead_away_from_the_agent_and_tells_the_supervisor()
    {
        var supervisor = Guid.NewGuid();
        var newAgent = Guid.NewGuid();
        var agency = Guid.NewGuid();

        var lead = await SeedLeadAsync(consent: true, ownerId: _colleague, agencyId: agency);
        var conversationId = await SeedPendingAsync(lead.Id, target: _colleague, deadline: _now.AddMinutes(-1));

        _admin.ResolveAgencyRecipientAsync(_tenantId, agency, null, Arg.Any<CancellationToken>()).Returns(supervisor);

        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<DispatchLeadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new DispatchLeadResult(Guid.NewGuid(), newAgent, "Awa", 80, _now.AddHours(4))));

        await using (var db = _db.CreateContext())
        {
            var services = new ServiceCollection()
                .AddSingleton(db)
                .AddSingleton<TimeProvider>(new FixedClock(_now))
                .AddSingleton(_admin)
                .AddSingleton(sender)
                .AddSingleton(_settings)
                .AddSingleton(Substitute.For<IIntegrationModule>())
                .AddSingleton(Substitute.For<INotificationsModule>())
                .AddKeyedSingleton(nameof(LeadsDbContext), _publisher)
                .BuildServiceProvider();

            await new WhatsAppTakeoverJob(Substitute.For<IServiceScopeFactory>(), NullLogger<WhatsAppTakeoverJob>.Instance)
                .RunAsync(services, _tenantId, CancellationToken.None);
        }

        await sender.Received(1).Send(
            Arg.Is<DispatchLeadCommand>(c => c.ExcludedAgentIds!.Contains(_colleague) && !c.QueueWhenNoAgent),
            Arg.Any<CancellationToken>());

        await using var read = _db.CreateContext();
        var conversation = await read.WhatsAppConversations.SingleAsync(c => c.Id == conversationId);
        conversation.TargetAgentId.Should().Be(newAgent);
        conversation.TakeoverDeadline.Should().Be(_now.AddMinutes(30), "the new agent gets a fresh SLA");

        await _publisher.Received(1).PublishAsync(
            Arg.Is<WhatsAppTakeoverBreachedEvent>(e => e.BreachedAgentId == _colleague && e.NewAgentId == newAgent && e.SupervisorId == supervisor),
            Arg.Any<CancellationToken>());
    }

    // ── Inbox ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_agent_sees_their_own_queue_a_supervisor_the_team_s_and_the_unplaced_ones()
    {
        var mineLead = await SeedLeadAsync(consent: true, ownerId: _me);
        var otherLead = await SeedLeadAsync(consent: true, ownerId: Guid.NewGuid(), phone: "+221770000002");

        await SeedPendingAsync(mineLead.Id, target: _me, deadline: _now.AddMinutes(10));
        await SeedPendingAsync(otherLead.Id, target: _colleague, deadline: _now.AddMinutes(5), connection: Guid.NewGuid());
        await SeedPendingAsync(null, target: null, deadline: null, connection: Guid.NewGuid());

        var asAgent = await InboxAsync(maySupervise: false);
        asAgent.Items.Should().ContainSingle(i => i.LeadId == mineLead.Id);

        var asSupervisor = await InboxAsync(maySupervise: true);
        asSupervisor.Items.Should().HaveCount(3, "the team's conversation and the one nobody is expected on");
        asSupervisor.Items[0].LeadId.Should().Be(otherLead.Id, "sorted by the SLA deadline, the most urgent first");
        asSupervisor.Counts["ToHandle"].Should().Be(3);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private WhatsAppConversation Conversation()
        => WhatsAppConversation.Open(_tenantId, _connectionId, "+221771234567", "idx", null, null, null, false, _now);

    private InboundMessagingMessage Inbound(string id, string text, int minutes = 0)
        => new(id, "+221771234567", "Awa", _now.AddMinutes(minutes - 5), "text", text, null, null, null, null);

    private async Task<Lead> SeedLeadAsync(bool consent, Guid? ownerId = null, Guid? agencyId = null, string? phone = null)
    {
        await using var db = _db.CreateContext();

        var lead = Lead.Capture(
            tenantId: _tenantId, fullName: "Awa Ndiaye", phoneNumber: phone ?? "+221771234567",
            source: LeadSource.Web, interestedProduct: "Crédit", preferredLanguage: "fr",
            location: new Sankore.Shared.Kernel.ValueObject.GeoPoint(14.7, -17.4),
            preferredAgencyId: agencyId, clock: TimeProvider.System, ownerId: ownerId, agencyId: agencyId);

        db.Leads.Add(lead);

        if (consent)
        {
            db.LeadConsents.Add(LeadConsent.Create(
                _tenantId, lead.Id, ConsentType.WhatsAppContact, ConsentChannel.WebForm, _me, TimeProvider.System, "Formulaire"));
        }

        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<Guid> SeedConversationAsync(Guid leadId, Guid holder, bool windowOpen, Guid? connection = null)
    {
        await using var db = _db.CreateContext();

        var conversation = WhatsAppConversation.Open(
            _tenantId, connection ?? _connectionId, "+221771234567", Guid.NewGuid().ToString(), leadId, null, null, false, _now.AddDays(-2));

        conversation.RecordInbound(windowOpen ? _now.AddHours(-1) : _now.AddDays(-2), null);
        conversation.TakeOver(holder, _now);

        db.WhatsAppConversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private async Task<Guid> SeedPendingAsync(Guid? leadId, Guid? target, DateTimeOffset? deadline, Guid? connection = null)
    {
        await using var db = _db.CreateContext();

        var conversation = WhatsAppConversation.Open(
            _tenantId, connection ?? _connectionId, "+221771234567", Guid.NewGuid().ToString(), leadId, null, "Contact", false, _now.AddHours(-1));

        conversation.RecordInbound(_now.AddMinutes(-40), null);
        conversation.Escalate("CUSTOMER_REPLIED", null, target, null, _now.AddMinutes(-40));

        if (deadline is { } d)
            typeof(WhatsAppConversation).GetProperty(nameof(WhatsAppConversation.TakeoverDeadline))!.SetValue(conversation, d);

        db.WhatsAppConversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    /// <summary>Lot 2's escalation, over the test context — the handler's own dependency.</summary>
    private WhatsAppEscalation Escalation(LeadsDbContext db) =>
        new(db, _customers, _settings, _publisher, NullLogger<WhatsAppEscalation>.Instance);

    /// <summary>No model on this deployment, so no bot: an inbound message escalates to a person.</summary>
    private WhatsAppBotProfiles BotProfiles(LeadsDbContext db) =>
        new(db, _settings, new UnavailableWhatsAppBotModel());

    private async Task RecordAsync(InboundMessagingMessage message)
    {
        await using var db = _db.CreateContext();

        await new RecordMessagingEventsHandler(
                db, _phoneIndexer, _blindIndexer, _customers, _settings, _realtime, _publisher,
                Escalation(db), BotProfiles(db),
            WhatsAppTestServices.Prospects(db, _phoneIndexer, _publisher, WhatsAppTestServices.FieldValues(_admin)),
            WhatsAppTestServices.FieldValues(_admin),
                new WhatsAppAudit(Substitute.For<IAuditWriter>(), TimeProvider.System, NullLogger<WhatsAppAudit>.Instance),
                new FixedClock(_now), NullLogger<RecordMessagingEventsHandler>.Instance)
            .Handle(new RecordMessagingEventsCommand(_tenantId, _connectionId, null, new MessagingEventBatch([message], [])), CancellationToken.None);
    }

    private async Task<Result<Guid>> ReplyAsync(Guid leadId, Guid conversationId, string text)
    {
        await using var db = _db.CreateContext();

        var loader = new WhatsAppConversationLoader(db, new WhatsAppLeadAccess(db, new LeadAccessGuard(_currentUser, _admin)));

        return await new SendWhatsAppTextHandler(db, loader, _currentUser, _publisher, new FixedClock(_now))
            .Handle(new SendWhatsAppTextCommand(leadId, conversationId, text, MayReply: true), CancellationToken.None);
    }

    private async Task<WhatsAppInboxDto> InboxAsync(bool maySupervise)
    {
        await using var db = _db.CreateContext();

        var result = await new GetWhatsAppInboxHandler(db, _currentUser, _admin, _customers, new WhatsAppPerimeter(db, _currentUser, _admin))
            .Handle(new GetWhatsAppInboxQuery(WhatsAppInboxTab.ToHandle, null, null, false, null, 20, true, maySupervise), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }
}
