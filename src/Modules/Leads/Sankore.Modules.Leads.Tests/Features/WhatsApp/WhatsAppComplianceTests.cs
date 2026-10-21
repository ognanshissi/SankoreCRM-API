namespace Sankore.Modules.Leads.Tests.Features.WhatsApp;

using System.Security.Cryptography;
using FluentAssertions;
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
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.WhatsAppMedia;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Realtime;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>Lot 4: media storage, retention, audit, the conversation read API, the handover history and the metrics.</summary>
public sealed class WhatsAppComplianceTests : IDisposable
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
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly IMessagingGateway _messaging = Substitute.For<IMessagingGateway>();
    private readonly InMemoryObjectBackend _backend = new();
    private readonly IBlindIndexer _blindIndexer =
        new HmacBlindIndexer(Options.Create(new FieldProtectionOptions { SectionName = "Leads", FieldEncryptionKey = Key, BlindIndexKey = Key }));
    private readonly IPhoneBlindIndexer _phoneIndexer = new HmacPhoneBlindIndexer(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Leads:BlindIndexKey"] = Key }).Build());

    public WhatsAppComplianceTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Roles.Returns(["SalesManager"]);

        _integration.Messaging.Returns(_messaging);
        _messaging.DeleteMediaAsync(default, default, default!, default).ReturnsForAnyArgs(IntegrationResult.Ok());
        _customers.FindMatchesAsync(default, default, default, default, default).ReturnsForAnyArgs([]);
        _admin.GetUserNamesAsync(default, default!, default).ReturnsForAnyArgs(new Dictionary<Guid, string>());
        _admin.GetTeamAgentIdsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([_colleague]);
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.WhatsAppTakeoverSlaMinutes, Arg.Any<CancellationToken>()).Returns(30);
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.WhatsAppRetentionMonths, Arg.Any<CancellationToken>()).Returns(24);
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.WhatsAppMediaRetentionDays, Arg.Any<CancellationToken>()).Returns(180);
        _settings.GetStringAsync(_tenantId, LeadSettingKeys.WhatsAppWaitingMessage, Arg.Any<CancellationToken>()).Returns("");
        _settings.GetStringAsync(_tenantId, LeadSettingKeys.WhatsAppOptOutConfirmation, Arg.Any<CancellationToken>()).Returns("");
        _settings.GetStringAsync(_tenantId, LeadSettingKeys.WhatsAppTemplateUnitPrices, Arg.Any<CancellationToken>()).Returns("{\"MARKETING\": 45}");
        _settings.GetStringAsync(_tenantId, LeadSettingKeys.WhatsAppPriceCurrency, Arg.Any<CancellationToken>()).Returns("XOF");
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private EncryptedWhatsAppMediaStore Store() => new(
        _backend,
        Options.Create(new WhatsAppMediaStorageOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }),
        NullLogger<EncryptedWhatsAppMediaStore>.Instance);

    // ── Media store (WA-BE-20) ─────────────────────────────────────────────────

    [Fact]
    public async Task A_media_is_stored_as_ciphertext_and_opens_only_for_its_tenant()
    {
        var store = Store();
        var photo = "a photograph of an identity card"u8.ToArray();

        var stored = await store.StoreAsync(_tenantId, photo, CancellationToken.None);

        var raw = _backend.Peek(_backend.Keys.Single())!;
        raw.AsSpan().IndexOf("identity card"u8).Should().Be(-1, "nothing of the plaintext reaches the medium");

        (await store.OpenAsync(_tenantId, stored.StorageRef, CancellationToken.None)).Should().Equal(photo);
        (await store.OpenAsync(Guid.NewGuid(), stored.StorageRef, CancellationToken.None)).Should().BeNull();
        stored.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(photo)).ToLowerInvariant());
    }

    [Fact]
    public async Task Each_media_has_its_own_data_key_and_a_tampered_object_does_not_open()
    {
        var store = Store();
        var a = await store.StoreAsync(_tenantId, [1, 2, 3, 4], CancellationToken.None);
        var b = await store.StoreAsync(_tenantId, [1, 2, 3, 4], CancellationToken.None);

        var keys = _backend.Keys.ToList();
        var wrappedA = _backend.Peek(keys[0])!.AsSpan(4 + 12 + 16, 32).ToArray();
        var wrappedB = _backend.Peek(keys[1])!.AsSpan(4 + 12 + 16, 32).ToArray();
        wrappedA.Should().NotEqual(wrappedB);

        var tampered = _backend.Peek(keys[0])!.ToArray();
        tampered[^1] ^= 0xFF;
        _backend.Overwrite(keys[0], tampered);

        var refOfTampered = keys[0].Contains(a.StorageRef.Split('.')[2]) ? a.StorageRef : b.StorageRef;
        var open = () => store.OpenAsync(_tenantId, refOfTampered, CancellationToken.None);
        await open.Should().ThrowAsync<CryptographicException>();
    }

    // ── Retention (WA-BE-21) ───────────────────────────────────────────────────

    [Fact]
    public async Task A_leadless_conversation_is_anonymised_after_the_retention_and_a_lead_s_is_left_to_the_lead()
    {
        var leadless = await SeedConversationAsync(leadId: null, lastMessage: _now.AddMonths(-25));
        var recent = await SeedConversationAsync(leadId: null, lastMessage: _now.AddMonths(-2), phone: "+221770000002");
        var ofALead = await SeedConversationAsync(leadId: Guid.NewGuid(), lastMessage: _now.AddMonths(-30), phone: "+221770000003");

        var (conversations, _) = await RunRetentionAsync(Store());

        conversations.Should().Be(1);

        await using var read = _db.CreateContext();
        (await read.WhatsAppConversations.SingleAsync(c => c.Id == leadless)).AnonymizedAt.Should().Be(_now);
        (await read.WhatsAppConversations.SingleAsync(c => c.Id == recent)).AnonymizedAt.Should().BeNull();
        (await read.WhatsAppConversations.SingleAsync(c => c.Id == ofALead)).AnonymizedAt.Should().BeNull();
    }

    [Fact]
    public async Task An_old_media_is_deleted_here_and_at_the_provider_and_the_message_says_so()
    {
        var store = Store();
        var conversationId = await SeedConversationAsync(leadId: Guid.NewGuid(), lastMessage: _now.AddDays(-1));
        var stored = await store.StoreAsync(_tenantId, [9, 9, 9], CancellationToken.None);

        Guid oldId, freshId;
        await using (var db = _db.CreateContext())
        {
            var old = WhatsAppMessage.Inbound(_tenantId, conversationId, "wamid.old", WhatsAppMessageKind.Image, null,
                "media-old", "image/jpeg", null, null, _now.AddDays(-200), _now.AddDays(-200));
            old.AttachStoredMedia(stored.StorageRef, 3, "x", _now.AddDays(-200));

            var fresh = WhatsAppMessage.Inbound(_tenantId, conversationId, "wamid.fresh", WhatsAppMessageKind.Image, null,
                "media-fresh", "image/jpeg", null, null, _now.AddDays(-10), _now.AddDays(-10));

            db.WhatsAppMessages.AddRange(old, fresh);
            await db.SaveChangesAsync();
            (oldId, freshId) = (old.Id, fresh.Id);
        }

        var (_, media) = await RunRetentionAsync(store);

        media.Should().Be(1);
        _backend.Keys.Should().BeEmpty();
        await _messaging.Received(1).DeleteMediaAsync(_tenantId, _connectionId, "media-old", Arg.Any<CancellationToken>());

        await using var read = _db.CreateContext();
        var purged = await read.WhatsAppMessages.SingleAsync(m => m.Id == oldId);
        purged.MediaPurgedAt.Should().Be(_now);
        purged.HasMedia.Should().BeFalse();
        (await read.WhatsAppMessages.SingleAsync(m => m.Id == freshId)).HasMedia.Should().BeTrue();
    }

    [Fact]
    public async Task A_zero_retention_skips_rather_than_deletes_everything()
    {
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.WhatsAppRetentionMonths, Arg.Any<CancellationToken>()).Returns(0);
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.WhatsAppMediaRetentionDays, Arg.Any<CancellationToken>()).Returns(0);
        await SeedConversationAsync(leadId: null, lastMessage: _now.AddYears(-5));

        (await RunRetentionAsync(Store())).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_stored_copy_with_no_store_to_reach_it_is_not_marked_purged()
    {
        var conversationId = await SeedConversationAsync(leadId: null, lastMessage: _now.AddDays(-1));

        await using (var db = _db.CreateContext())
        {
            var old = WhatsAppMessage.Inbound(_tenantId, conversationId, "wamid.x", WhatsAppMessageKind.Image, null,
                null, "image/jpeg", null, null, _now.AddDays(-400), _now.AddDays(-400));
            old.AttachStoredMedia("m1.0000000000000000.00000000000000000000000000000000", 3, "x", _now.AddDays(-400));
            db.WhatsAppMessages.Add(old);
            await db.SaveChangesAsync();
        }

        (await RunRetentionAsync(store: null)).Media.Should().Be(0);

        await using var read = _db.CreateContext();
        (await read.WhatsAppMessages.SingleAsync()).MediaPurgedAt.Should().BeNull();
    }

    // ── Handover history and metrics (WA-BE-22) ───────────────────────────────

    [Fact]
    public async Task An_escalation_opens_a_handover_and_the_takeover_closes_it()
    {
        var lead = await SeedLeadAsync(ownerId: _colleague);
        await RecordAsync(Inbound("wamid.1", "Bonjour"));

        await using (var read = _db.CreateContext())
        {
            var open = await read.WhatsAppHandovers.SingleAsync();
            open.Reason.Should().Be("CUSTOMER_REPLIED");
            open.LeadId.Should().Be(lead.Id);
            open.IsOpen.Should().BeTrue();
        }

        await using (var db = _db.CreateContext())
        {
            var conversation = await db.WhatsAppConversations.SingleAsync();
            var loader = new WhatsAppConversationLoader(db, new WhatsAppLeadAccess(db, new LeadAccessGuard(_currentUser, _admin)));

            (await new TakeOverWhatsAppHandler(db, loader, _currentUser, _realtime, new FixedClock(_now.AddMinutes(12)),
                    NullLogger<TakeOverWhatsAppHandler>.Instance)
                .Handle(new TakeOverWhatsAppCommand(lead.Id, conversation.Id, MayReply: true), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        await using var after = _db.CreateContext();
        var closed = await after.WhatsAppHandovers.SingleAsync();
        closed.EndedBy.Should().Be(WhatsAppHandoverEnd.TakenOver);
        closed.TakenOverBy.Should().Be(_me);
        (closed.EndedAt!.Value - closed.EscalatedAt).Should().Be(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public async Task The_metrics_count_answered_templates_escalations_takeover_delay_bot_resolution_and_cost()
    {
        await using (var db = _db.CreateContext())
        {
            var template = WhatsAppTemplate.Discover(_tenantId, _connectionId, "relance", "fr", _now);
            template.Refresh("marketing", "APPROVED", null, null, "Bonjour", null, [], _now);
            db.WhatsAppTemplates.Add(template);

            var answered = Conversation("+221770000010");
            var silent = Conversation("+221770000011");
            var bot = Conversation("+221770000012");
            db.WhatsAppConversations.AddRange(answered, silent, bot);

            WhatsAppMessage Sent(WhatsAppConversation c, DateTimeOffset at) =>
                WhatsAppMessage.OutboundTemplate(_tenantId, c.Id, _me, template.Id, "relance", "fr", "Bonjour", "[]", at);

            db.WhatsAppMessages.AddRange(
                Sent(answered, _now.AddDays(-3)),
                WhatsAppMessage.Inbound(_tenantId, answered.Id, "w1", WhatsAppMessageKind.Text, "Oui", null, null, null, null, _now.AddDays(-3).AddHours(2), _now),
                Sent(silent, _now.AddDays(-3)),
                WhatsAppMessage.Inbound(_tenantId, silent.Id, "w2", WhatsAppMessageKind.Text, "Trop tard", null, null, null, null, _now.AddDays(-1), _now),
                WhatsAppMessage.Inbound(_tenantId, bot.Id, "w3", WhatsAppMessageKind.Text, "Question", null, null, null, null, _now.AddDays(-2), _now),
                WhatsAppMessage.OutboundText(_tenantId, bot.Id, WhatsAppAuthor.Bot, null, "Réponse", _now.AddDays(-2)));

            db.WhatsAppBotTurns.Add(WhatsAppBotTurn.Record(_tenantId, bot.Id, Guid.NewGuid(), null, 1, "fake", null, "Réponse", null,
                WhatsAppBotTurnOutcome.Replied, null, true, 800, null, null, null, null, _now.AddDays(-2)));

            var h1 = WhatsAppHandover.Open(_tenantId, answered.Id, null, "CUSTOMER_REPLIED", false, _now.AddDays(-3).AddHours(2).AddMinutes(30), _now.AddDays(-3).AddHours(2));
            h1.End(WhatsAppHandoverEnd.TakenOver, _me, _now.AddDays(-3).AddHours(2).AddMinutes(10));
            var h2 = WhatsAppHandover.Open(_tenantId, silent.Id, null, "CUSTOMER_REPLIED", false, _now.AddDays(-1).AddMinutes(30), _now.AddDays(-1));
            h2.MarkBreached(_now.AddDays(-1).AddMinutes(31));
            h2.End(WhatsAppHandoverEnd.TakenOver, _me, _now.AddDays(-1).AddMinutes(50));
            db.WhatsAppHandovers.AddRange(h1, h2);

            await db.SaveChangesAsync();
        }

        await using var ctx = _db.CreateContext();
        var metrics = (await new GetWhatsAppMetricsHandler(ctx, new WhatsAppPerimeter(ctx, BypassUser(), _admin), _currentUser, _settings, new FixedClock(_now))
            .Handle(new GetWhatsAppMetricsQuery(null, null), CancellationToken.None)).Value!;

        metrics.TemplatesSent.Should().Be(2);
        metrics.TemplatesAnswered.Should().Be(1, "an answer two days later is not an answer to the template");
        metrics.TemplateResponseRate.Should().Be(0.5);
        metrics.ConversationsWithInbound.Should().Be(3);
        metrics.Escalations.Should().Be(2);
        metrics.TakenOver.Should().Be(2);
        metrics.AverageTakeoverMinutes.Should().Be(30);
        metrics.MedianTakeoverMinutes.Should().Be(30);
        metrics.Breaches.Should().Be(1);
        metrics.WithinSlaRate.Should().Be(0.5);
        metrics.BotConversations.Should().Be(1);
        metrics.BotResolved.Should().Be(1);
        metrics.CostByCategory.Should().ContainEquivalentOf(new WhatsAppMetricCostDto("MARKETING", 2, 45, 90));
        metrics.CostByCategory.Single(c => c.Category == "SERVICE").EstimatedCost.Should().BeNull();
        metrics.EstimatedTotalCost.Should().BeNull("a total that leaves out an unpriced category would read as complete");
    }

    [Fact]
    public void Unit_prices_are_read_leniently_and_a_bad_entry_does_not_blank_the_others()
    {
        var prices = GetWhatsAppMetricsHandler.ParsePrices("{\"marketing\": 45, \"UTILITY\": \"10,5\", \"AUTHENTICATION\": \"abc\"}");
        prices.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["MARKETING"] = 45, ["UTILITY"] = 10.5m });
        GetWhatsAppMetricsHandler.ParsePrices("not json").Should().BeEmpty();
    }

    // ── Read API (WA-BE-19) ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData("40", 40, false)]
    [InlineData("o:20", 20, false)]
    [InlineData("k:638000000000000000:638000000000000001", 0, true)]
    [InlineData("k:-1:2", 0, false)]
    [InlineData("garbage", 0, false)]
    public void The_inbox_cursor_reads_both_shapes_and_the_lot_2_one(string? cursor, int offset, bool keyset)
    {
        var decoded = InboxCursor.Decode(cursor);
        decoded.Offset.Should().Be(offset);
        (decoded.Keyset is not null).Should().Be(keyset);
    }

    [Fact]
    public async Task The_closed_tab_pages_by_keyset_without_repeating_or_skipping()
    {
        await using (var db = _db.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                var c = Conversation($"+22177000002{i}");
                c.RecordInbound(_now.AddMinutes(-i), null);
                c.Close("fini", _me, _now);
                db.WhatsAppConversations.Add(c);
            }

            await db.SaveChangesAsync();
        }

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            await using var db = _db.CreateContext();
            var page = (await new GetWhatsAppInboxHandler(db, BypassUser(), _admin, _customers, new WhatsAppPerimeter(db, BypassUser(), _admin))
                .Handle(new GetWhatsAppInboxQuery(WhatsAppInboxTab.Closed, null, null, false, cursor, 2, true, true), CancellationToken.None)).Value!;

            seen.AddRange(page.Items.Select(i => i.ConversationId));
            cursor = page.NextCursor;
            cursor?.Should().StartWith("k:");
        }
        while (cursor is not null);

        seen.Should().HaveCount(5).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_supervisor_files_an_unattached_conversation_on_a_lead_once()
    {
        var lead = await SeedLeadAsync(ownerId: _me);
        var conversationId = await SeedConversationAsync(leadId: null, lastMessage: _now);

        AttachWhatsAppConversationHandler Handler(LeadsDbContext db) =>
            new(db, new WhatsAppPerimeter(db, _currentUser, _admin), new LeadAccessGuard(_currentUser, _admin));

        await using (var db = _db.CreateContext())
        {
            (await Handler(db).Handle(new AttachWhatsAppConversationCommand(conversationId, lead.Id, true, MaySupervise: true), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        await using (var db = _db.CreateContext())
        {
            (await Handler(db).Handle(new AttachWhatsAppConversationCommand(conversationId, lead.Id, true, MaySupervise: true), CancellationToken.None))
                .Error.Should().Be(WhatsAppErrors.AlreadyAttached);
        }

        await using var read = _db.CreateContext();
        (await read.WhatsAppConversations.SingleAsync()).LeadId.Should().Be(lead.Id);
    }

    [Fact]
    public async Task An_agent_cannot_open_a_conversation_outside_their_perimeter_and_learns_nothing_of_it()
    {
        var conversationId = await SeedConversationAsync(leadId: null, lastMessage: _now);
        _currentUser.Roles.Returns(["Agent"]);

        await using var db = _db.CreateContext();
        var result = await new GetWhatsAppConversationHandler(new WhatsAppPerimeter(db, _currentUser, _admin), _currentUser, _admin, _customers, Audit())
            .Handle(new GetWhatsAppConversationQuery(conversationId, HasWhatsAppPermission: true, MaySupervise: false), CancellationToken.None);

        result.Error.Should().Be(WhatsAppConversationErrors.ConversationNotFound);
    }

    [Fact]
    public async Task The_lead_tab_presents_an_open_conversation_before_a_more_recent_closed_one()
    {
        var lead = await SeedLeadAsync(ownerId: _me);

        await using (var db = _db.CreateContext())
        {
            var held = Conversation("+221770000031");
            held.AttachToLead(lead.Id);
            held.RecordInbound(_now.AddHours(-2), null);
            held.TakeOver(_me, _now.AddHours(-2));

            var closed = Conversation("+221770000032");
            closed.AttachToLead(lead.Id);
            closed.RecordInbound(_now.AddMinutes(-5), null);
            closed.Close("fini", _me, _now);

            db.WhatsAppConversations.AddRange(held, closed);
            await db.SaveChangesAsync();
        }

        await using var ctx = _db.CreateContext();
        var panel = (await new GetWhatsAppPanelHandler(
                ctx, new WhatsAppLeadAccess(ctx, new LeadAccessGuard(_currentUser, _admin)), _currentUser, _admin, _integration,
                _blindIndexer, new FixedClock(_now))
            .Handle(new GetWhatsAppPanelQuery(lead.Id, true, false), CancellationToken.None)).Value!;

        panel.Conversation!.Status.Should().Be("Human", "a held conversation must not hide behind a closed one");
        panel.Conversation.MaskedPhone.Should().EndWith("31", "the header shows the number THIS conversation runs on");
    }

    // ── Audit (WA-BE-20) ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_supervisor_reading_a_colleague_s_thread_is_audited_and_its_owner_is_not()
    {
        var lead = await SeedLeadAsync(ownerId: _colleague);
        await RecordAsync(Inbound("wamid.1", "Bonjour"));
        _auditWriter.ClearReceivedCalls();

        await ReadThreadAsync(lead.Id, _currentUser);

        await _auditWriter.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == WhatsAppAudit.ConversationConsulted && e.UserId == _me), Arg.Any<CancellationToken>());

        _auditWriter.ClearReceivedCalls();
        var owner = Substitute.For<ICurrentUser>();
        owner.Id.Returns(_colleague);
        owner.TenantId.Returns(_tenantId);
        owner.Roles.Returns(["Agent"]);

        await ReadThreadAsync(lead.Id, owner);

        await _auditWriter.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_STOP_and_a_first_message_s_consent_are_audited_under_SYSTEM()
    {
        await SeedLeadAsync(ownerId: _colleague, consent: false);

        await RecordAsync(Inbound("wamid.1", "Bonjour"));
        await RecordAsync(Inbound("wamid.2", "STOP", minutes: 1));

        await _auditWriter.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == WhatsAppAudit.ConsentFromInboundMessage && e.UserId == Guid.Empty), Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == WhatsAppAudit.OptOutReceived && e.UserId == Guid.Empty), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_inbound_media_asks_for_its_encrypted_copy()
    {
        await SeedLeadAsync(ownerId: _colleague);

        await RecordAsync(new InboundMessagingMessage("wamid.img", "+221771234567", "Awa", _now.AddMinutes(-5), "image", null,
            "media-1", "image/jpeg", null, null));

        await _publisher.Received(1).PublishAsync(Arg.Any<WhatsAppMediaReceivedEvent>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private ICurrentUser BypassUser()
    {
        var admin = Substitute.For<ICurrentUser>();
        admin.Id.Returns(_me);
        admin.TenantId.Returns(_tenantId);
        admin.Roles.Returns(["Administrator"]);
        return admin;
    }

    private WhatsAppAudit Audit() => new(_auditWriter, new FixedClock(_now), NullLogger<WhatsAppAudit>.Instance);

    private WhatsAppConversation Conversation(string phone) =>
        WhatsAppConversation.Open(_tenantId, _connectionId, phone, _blindIndexer.Compute(BlindIndexPurpose.Phone, phone), null, null, null, false, _now.AddDays(-5));

    private async Task<Guid> SeedConversationAsync(Guid? leadId, DateTimeOffset lastMessage, string phone = "+221770000001")
    {
        await using var db = _db.CreateContext();
        var conversation = WhatsAppConversation.Open(
            _tenantId, _connectionId, phone, _blindIndexer.Compute(BlindIndexPurpose.Phone, phone), leadId, null, "Client", false, lastMessage);
        db.WhatsAppConversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    private async Task<Lead> SeedLeadAsync(Guid? ownerId, bool consent = true)
    {
        await using var db = _db.CreateContext();

        var lead = Lead.Capture(
            tenantId: _tenantId, fullName: "Awa Ndiaye", phoneNumber: "+221771234567",
            source: LeadSource.Web, interestedProduct: "Crédit", preferredLanguage: "fr",
            location: new Sankore.Shared.Kernel.ValueObject.GeoPoint(14.7, -17.4),
            preferredAgencyId: null, clock: TimeProvider.System, ownerId: ownerId);

        db.Leads.Add(lead);

        if (consent)
        {
            db.LeadConsents.Add(LeadConsent.Create(
                _tenantId, lead.Id, ConsentType.WhatsAppContact, ConsentChannel.WebForm, _me, TimeProvider.System, "Formulaire"));
        }

        await db.SaveChangesAsync();
        return lead;
    }

    private InboundMessagingMessage Inbound(string id, string text, int minutes = 0)
        => new(id, "+221771234567", "Awa", _now.AddMinutes(minutes - 5), "text", text, null, null, null, null);

    private async Task RecordAsync(InboundMessagingMessage message)
    {
        await using var db = _db.CreateContext();

        await new RecordMessagingEventsHandler(
                db, _phoneIndexer, _blindIndexer, _customers, _settings, _realtime, _publisher,
                new WhatsAppEscalation(db, _customers, _settings, _publisher, NullLogger<WhatsAppEscalation>.Instance),
                new WhatsAppBotProfiles(db, _settings, new UnavailableWhatsAppBotModel()),
                Audit(),
                new FixedClock(_now), NullLogger<RecordMessagingEventsHandler>.Instance)
            .Handle(new RecordMessagingEventsCommand(_tenantId, _connectionId, null, new MessagingEventBatch([message], [])), CancellationToken.None);
    }

    private async Task ReadThreadAsync(Guid leadId, ICurrentUser user)
    {
        await using var db = _db.CreateContext();
        var access = new WhatsAppLeadAccess(db, new LeadAccessGuard(user, _admin));

        (await new ListWhatsAppMessagesHandler(db, access, user, _admin, Audit())
            .Handle(new ListWhatsAppMessagesQuery(leadId, true, null, 30), CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    private async Task<(int Conversations, int Media)> RunRetentionAsync(IWhatsAppMediaStore? store)
    {
        await using var db = _db.CreateContext();

        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<TimeProvider>(new FixedClock(_now))
            .AddSingleton(_settings)
            .AddSingleton(_integration);

        if (store is not null) services.AddSingleton(store);

        return await new WhatsAppRetentionJob(Substitute.For<IServiceScopeFactory>(), NullLogger<WhatsAppRetentionJob>.Instance)
            .RunAsync(services.BuildServiceProvider(), _tenantId, CancellationToken.None);
    }
}
