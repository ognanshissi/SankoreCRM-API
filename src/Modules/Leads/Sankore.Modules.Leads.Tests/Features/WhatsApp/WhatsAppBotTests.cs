namespace Sankore.Modules.Leads.Tests.Features.WhatsApp;

using System.Text.Json;
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
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Realtime;
using Xunit;

/// <summary>Lot 3: the bot's rules, its minimisation, its profiles and its turns.</summary>
public sealed class WhatsAppBotTests : IDisposable
{
    private const string Key = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly TestDbContextFactory _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ILeadSettings _settings = Substitute.For<ILeadSettings>();
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();
    private readonly ILeadEmailIndexer _emailIndexer = Substitute.For<ILeadEmailIndexer>();
    private readonly IBlindIndexer _blindIndexer =
        new HmacBlindIndexer(Options.Create(new FieldProtectionOptions { SectionName = "Leads", FieldEncryptionKey = Key, BlindIndexKey = Key }));
    private readonly IPhoneBlindIndexer _phoneIndexer = new HmacPhoneBlindIndexer(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Leads:BlindIndexKey"] = Key }).Build());

    public WhatsAppBotTests()
    {
        _db = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenantId);

        _customers.FindMatchesAsync(default, default, default, default, default).ReturnsForAnyArgs([]);
        _admin.ListProductsAsync(_tenantId, null, Arg.Any<CancellationToken>()).Returns([]);
        _admin.GetUserNamesAsync(default, default!, default).ReturnsForAnyArgs(new Dictionary<Guid, string>());
        _emailIndexer.Compute(Arg.Any<string?>()).Returns(c => c.Arg<string?>() is null ? null : "idx");
        _settings.GetIntAsync(_tenantId, LeadSettingKeys.WhatsAppTakeoverSlaMinutes, Arg.Any<CancellationToken>()).Returns(30);
        _settings.GetStringAsync(_tenantId, LeadSettingKeys.WhatsAppWaitingMessage, Arg.Any<CancellationToken>()).Returns("Un conseiller arrive.");
        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppBotEnabled, Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers each call with the next step; the last step repeats.</summary>
    private sealed class ScriptedModel(params Func<BotModelRequest, CancellationToken, Task<BotModelResponse>>[] steps) : IWhatsAppBotModel
    {
        public List<BotModelRequest> Requests { get; } = [];
        public bool IsAvailable => true;
        public string Name => "scripted";
        public string Provider => "Test";

        public Task<BotModelResponse> CompleteAsync(BotModelRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return steps[Math.Min(Requests.Count - 1, steps.Length - 1)](request, ct);
        }
    }

    private static Func<BotModelRequest, CancellationToken, Task<BotModelResponse>> Tool(string name, object input) =>
        (_, _) => Task.FromResult(new BotModelResponse(
            [new BotToolUseBlock($"toolu_{Guid.NewGuid():N}", name, JsonSerializer.SerializeToElement(input))], "tool_use", 12, 7));

    // ── Rules (WA-BE-13) ───────────────────────────────────────────────────────

    private WhatsAppBotProfile Profile(
        IReadOnlyList<string>? keywords = null, int maxTurns = 2, int windowMinutes = 120,
        IReadOnlyList<string>? fields = null, IReadOnlyList<WhatsAppBotOpeningSlot>? hours = null) =>
        WhatsAppBotProfile.Create(
            _tenantId, Guid.Empty, 1, isEnabled: true, "Bonjour, je suis l’assistant.", "Nos conseillers reviennent demain.",
            "Sois bref.", [new("Quels documents pour un crédit ?", "Une pièce d’identité et un justificatif de revenus.")],
            fields ?? [], hours, null, keywords ?? WhatsAppBotProfile.DefaultKeywords, maxTurns, windowMinutes, _me, _now).Value!;

    [Theory]
    [InlineData("Je veux parler à un conseiller", WhatsAppBotReasons.HumanRequested)]
    [InlineData("Rappelez-moi s'il vous plaît", WhatsAppBotReasons.HumanRequested)]
    [InlineData("Quel est le TAUX ?", WhatsAppBotReasons.SensitiveKeyword)]
    [InlineData("J'ai deux réclamations à faire", WhatsAppBotReasons.SensitiveKeyword)]
    [InlineData("Pouvez-vous me conseiller sur l'épargne ?", null)]
    [InlineData("Quels documents faut-il ?", null)]
    [InlineData("Je trouve ça tautologique", null)]
    public void The_rules_read_whole_words_accent_free(string text, string? reason)
        => WhatsAppBotRules.BeforeModel(WhatsAppMessageKind.Text, text, LeadStatus.New, 0, Profile())?.Reason.Should().Be(reason);

    [Fact]
    public void A_voice_note_a_qualified_lead_and_the_unanswered_counter_hand_over()
    {
        WhatsAppBotRules.BeforeModel(WhatsAppMessageKind.Audio, null, LeadStatus.New, 0, Profile())!
            .Reason.Should().Be(WhatsAppBotReasons.UnsupportedMessage);

        WhatsAppBotRules.BeforeModel(WhatsAppMessageKind.Text, "Bonjour", LeadStatus.Qualified, 0, Profile())!
            .Reason.Should().Be(WhatsAppBotReasons.LeadQualified);

        WhatsAppBotRules.BeforeModel(WhatsAppMessageKind.Text, "Bonjour", LeadStatus.New, 2, Profile(maxTurns: 2))!
            .Reason.Should().Be(WhatsAppBotReasons.NoUsefulAnswer);

        WhatsAppBotRules.BeforeModel(WhatsAppMessageKind.Text, "Bonjour", LeadStatus.New, 5, Profile(maxTurns: 0))
            .Should().BeNull("0 switches the counter rule off");
    }

    [Fact]
    public void The_window_rule_fires_only_inside_the_threshold()
    {
        var profile = Profile(windowMinutes: 120);

        WhatsAppBotRules.WindowExpiring(_now.AddMinutes(90), profile, _now)!.Reason.Should().Be(WhatsAppBotReasons.WindowExpiring);
        WhatsAppBotRules.WindowExpiring(_now.AddHours(3), profile, _now).Should().BeNull();
        WhatsAppBotRules.WindowExpiring(_now.AddMinutes(-1), profile, _now).Should().BeNull("a closed window has nobody left to answer freely");
        WhatsAppBotRules.WindowExpiring(_now.AddMinutes(10), Profile(windowMinutes: 0), _now).Should().BeNull();
    }

    // ── Minimisation (WA-BE-11) ────────────────────────────────────────────────

    [Theory]
    [InlineData("Mon numéro : +225 07 08 09 18 01", "Mon numéro : [numéro]")]
    [InlineData("Écrivez à awa.ndiaye@example.com", "Écrivez à [e-mail]")]
    [InlineData("Ma CNI CI0012345678", "Ma CNI [identifiant]")]
    [InlineData("Il me faut 15 000 000 FCFA", "Il me faut 15 000 000 FCFA")]
    [InlineData("Né le 12/10/1990", "Né le 12/10/1990")]
    [InlineData("500 000", "500 000")]
    public void What_leaves_the_platform_keeps_amounts_and_loses_identifiers(string text, string expected)
        => WhatsAppBotRedactor.Redact(text).Should().Be(expected);

    [Theory]
    [InlineData("500 000 FCFA", 500_000, "XOF")]
    [InlineData("1,5 million", 1_500_000, "XOF")]
    [InlineData("750k", 750_000, "XOF")]
    [InlineData("2.000.000", 2_000_000, "XOF")]
    [InlineData("3000 euros", 3_000, "EUR")]
    public void Amounts_are_read_the_way_customers_type_them(string text, decimal amount, string currency)
    {
        var money = WhatsAppBotEngine.ParseAmount(text);
        money!.Amount.Should().Be(amount);
        money.Currency.Should().Be(currency);
    }

    [Fact]
    public void A_text_with_no_number_is_no_amount() => WhatsAppBotEngine.ParseAmount("beaucoup").Should().BeNull();

    // ── Profiles (WA-BE-12) ────────────────────────────────────────────────────

    [Fact]
    public void A_profile_refuses_an_unknown_field_an_incomplete_FAQ_and_bad_hours()
    {
        Result<WhatsAppBotProfile> Create(IReadOnlyList<WhatsAppBotFaqEntry>? faq = null, IReadOnlyList<string>? fields = null,
            IReadOnlyList<WhatsAppBotOpeningSlot>? hours = null) =>
            WhatsAppBotProfile.Create(_tenantId, Guid.Empty, 1, true, null, null, null, faq, fields, hours, null, null, 2, 120, _me, _now);

        Create(fields: ["NATIONAL_ID"]).Error.Should().Be("WHATSAPP_BOT_FIELD_UNKNOWN");
        Create(faq: [new("Une question", "")]).Error.Should().Be("WHATSAPP_BOT_FAQ_INCOMPLETE");
        Create(hours: [new(DayOfWeek.Monday, "18:00", "08:00")]).Error.Should().Be("WHATSAPP_BOT_HOURS_INVALID");
        Create(fields: ["email", "CITY"]).Value!.FieldsToCollect.Should().Equal("EMAIL", "CITY");
    }

    [Fact]
    public void Agent_hours_are_read_in_the_profile_s_zone_and_none_declared_is_never_out_of_hours()
    {
        // Abidjan is UTC: a Wednesday at 09:00 UTC.
        var profile = Profile(hours: [new(DayOfWeek.Wednesday, "08:00", "17:00")]);

        profile.IsWithinAgentHours(_now).Should().BeTrue();
        profile.IsWithinAgentHours(_now.AddHours(10)).Should().BeFalse();
        Profile().IsWithinAgentHours(_now.AddHours(15)).Should().BeTrue();
    }

    [Fact]
    public async Task Saving_writes_a_new_version_and_a_stale_screen_is_refused()
    {
        await using var db = _db.CreateContext();
        var handler = new SaveWhatsAppBotProfileHandler(db, _admin, _currentUser, new FixedClock(_now));

        SaveWhatsAppBotProfileRequest Request(int expected, bool enabled) =>
            new(expected, enabled, "Bonjour", null, "Sois bref.", [new("Q ?", "R.")], ["EMAIL"], [new(1, "08:00", "17:00")],
                null, ["taux"], 2, 120);

        (await handler.Handle(new(Guid.Empty, Request(0, true)), CancellationToken.None)).Value!.Version.Should().Be(1);
        (await handler.Handle(new(Guid.Empty, Request(1, false)), CancellationToken.None)).Value!.Version.Should().Be(2);
        (await handler.Handle(new(Guid.Empty, Request(1, true)), CancellationToken.None)).Error.Should().Be(WhatsAppBotErrors.Conflict);

        var history = await new GetWhatsAppBotProfileHandler(db, _admin, _currentUser)
            .Handle(new GetWhatsAppBotProfileQuery(Guid.Empty, null), CancellationToken.None);

        history.Value!.Version.Should().Be(2);
        history.Value.IsEnabled.Should().BeFalse("switching the bot off is a version like any other");
        history.Value.History.Select(h => h.Version).Should().Equal(2, 1);
    }

    [Fact]
    public async Task A_product_with_its_own_disabled_profile_is_not_answered_by_the_general_one()
    {
        var line = Guid.NewGuid();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        await SaveProfileAsync(line, enabled: false);

        await using var db = _db.CreateContext();
        var lead = Lead.Capture(_tenantId, "Awa", "+221771234567", LeadSource.Web, "Crédit", "fr",
            new Sankore.Shared.Kernel.ValueObject.GeoPoint(0, 0), null, TimeProvider.System);
        lead.SetProductLine(line);

        var profiles = new WhatsAppBotProfiles(db, _settings, new FakeWhatsAppBotModel());
        (await profiles.ResolveActiveAsync(_tenantId, lead, CancellationToken.None)).Should().BeNull();

        lead.SetProductLine(null);
        (await profiles.ResolveActiveAsync(_tenantId, lead, CancellationToken.None))!.IsGeneral.Should().BeTrue();
    }

    // ── Routing (WA-BE-11) ─────────────────────────────────────────────────────

    [Fact]
    public void A_reply_to_an_agent_s_template_goes_to_a_person_even_with_a_bot()
    {
        var conversation = WhatsAppConversation.Open(_tenantId, _connectionId, "+221771234567", "idx", Guid.NewGuid(), null, null, false, _now);
        conversation.RecordTemplateSent(_now);

        conversation.RecordInbound(_now.AddMinutes(1), null, botAvailable: true).Should().Be(WhatsAppInboundRouting.Escalate);
    }

    [Fact]
    public void A_first_message_opens_the_bot_and_the_next_continues_it()
    {
        var conversation = WhatsAppConversation.Open(_tenantId, _connectionId, "+221771234567", "idx", Guid.NewGuid(), null, null, false, _now);

        conversation.RecordInbound(_now, null, botAvailable: true).Should().Be(WhatsAppInboundRouting.BotStarted);
        conversation.Status.Should().Be(WhatsAppConversationStatus.Bot);
        conversation.RecordInbound(_now.AddMinutes(1), null, botAvailable: true).Should().Be(WhatsAppInboundRouting.BotContinues);
        conversation.RecordInbound(_now.AddMinutes(2), null, botAvailable: false).Should().Be(WhatsAppInboundRouting.Escalate,
            "a bot switched off since hands over at the next message");
    }

    // ── Turns ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_first_message_is_welcomed_then_answered_by_the_bot()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(
            Tool(WhatsAppBotTools.GetProductInfo, new { }),
            Tool(WhatsAppBotTools.SendReply, new { text = "Une pièce d’identité et un justificatif de revenus.", answered_question = true }));

        var (conversationId, messageId) = await ReceiveAsync("Quels documents pour un crédit ? Mon numéro est le 0708091801", model);
        await RunTurnAsync(model, conversationId, messageId);

        await using var read = _db.CreateContext();
        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.Status.Should().Be(WhatsAppConversationStatus.Bot);

        var outbound = await read.WhatsAppMessages.Where(m => m.Direction == WhatsAppDirection.Outbound).OrderBy(m => m.OccurredAt).ToListAsync();
        outbound.Select(m => m.Author).Should().Equal(WhatsAppAuthor.Bot, WhatsAppAuthor.Bot);
        outbound[0].Body.Should().Be("Bonjour, je suis l’assistant.");
        outbound[1].Body.Should().Be("Une pièce d’identité et un justificatif de revenus.");

        var turn = await read.WhatsAppBotTurns.SingleAsync();
        turn.Outcome.Should().Be(WhatsAppBotTurnOutcome.Replied);
        turn.Model.Should().Be("scripted");
        turn.ProfileVersion.Should().Be(1);
        turn.Answered.Should().BeTrue();
        turn.InputTokens.Should().Be(24);
        turn.ReplyMessageId.Should().Be(outbound[1].Id);
        turn.Input.Should().Contain("[numéro]").And.NotContain("0708091801");

        model.Requests[0].Messages.SelectMany(m => m.Content).OfType<BotTextBlock>()
            .Should().NotContain(b => b.Text.Contains("0708091801"), "the number never leaves the platform");
        model.Requests[0].System.Should().Contain("Bonjour, je suis l’assistant.", "the model is told the welcome already went");
    }

    [Fact]
    public async Task The_model_s_handover_is_saved_with_its_reason_and_summary()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(Tool(WhatsAppBotTools.EscalateToHuman, new { reason = "ready_to_subscribe", summary = "Veut ouvrir un compte épargne." }));

        var (conversationId, messageId) = await ReceiveAsync("Je suis prêt, on fait comment ?", model);
        await RunTurnAsync(model, conversationId, messageId);

        await using var read = _db.CreateContext();
        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.EscalationReason.Should().Be("BOT_READY_TO_SUBSCRIBE");
        conversation.EscalationSummary.Should().Be("Veut ouvrir un compte épargne.");
        conversation.TakeoverDeadline.Should().Be(_now.AddMinutes(30));

        (await read.WhatsAppBotTurns.SingleAsync()).Outcome.Should().Be(WhatsAppBotTurnOutcome.Escalated);
        await _publisher.Received(1).PublishAsync(Arg.Any<WhatsAppConversationEscalatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_handover_without_a_reason_is_refused_to_the_model_which_must_try_again()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(
            Tool(WhatsAppBotTools.EscalateToHuman, new { summary = "Un client." }),
            Tool(WhatsAppBotTools.EscalateToHuman, new { reason = "complaint", summary = "Se plaint d’un retard." }));

        var (conversationId, messageId) = await ReceiveAsync("Ça ne va pas du tout", model);
        await RunTurnAsync(model, conversationId, messageId);

        model.Requests.Should().HaveCount(2);
        model.Requests[1].Messages[^1].Content.OfType<BotToolResultBlock>().Single().IsError.Should().BeTrue();

        await using var read = _db.CreateContext();
        (await read.WhatsAppConversations.SingleAsync()).EscalationReason.Should().Be("BOT_COMPLAINT");
    }

    [Fact]
    public async Task A_model_that_fails_hands_over_with_the_waiting_message()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel((_, _) => throw new BotModelException("BOT_MODEL_HTTP_529", "overloaded"));

        var (conversationId, messageId) = await ReceiveAsync("Bonjour", model);
        await RunTurnAsync(model, conversationId, messageId);

        await using var read = _db.CreateContext();
        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.EscalationReason.Should().Be(WhatsAppBotReasons.Failure);

        (await read.WhatsAppMessages.AnyAsync(m => m.Author == WhatsAppAuthor.System && m.Body == "Un conseiller arrive.")).Should().BeTrue();

        var turn = await read.WhatsAppBotTurns.SingleAsync();
        turn.Outcome.Should().Be(WhatsAppBotTurnOutcome.Failed);
        turn.ErrorCode.Should().Be("BOT_MODEL_HTTP_529");
    }

    [Fact]
    public async Task A_model_too_slow_for_the_turn_s_budget_is_a_failure()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            throw new InvalidOperationException("unreachable");
        });

        var (conversationId, messageId) = await ReceiveAsync("Bonjour", model);
        await RunTurnAsync(model, conversationId, messageId, timeoutSeconds: 1);

        await using var read = _db.CreateContext();
        (await read.WhatsAppBotTurns.SingleAsync()).ErrorCode.Should().Be("BOT_TIMEOUT");
        (await read.WhatsAppConversations.SingleAsync()).Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
    }

    [Fact]
    public async Task A_rule_decides_before_any_model_is_asked()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(Tool(WhatsAppBotTools.SendReply, new { text = "x", answered_question = true }));

        var (conversationId, messageId) = await ReceiveAsync("Je veux parler à un conseiller", model);
        await RunTurnAsync(model, conversationId, messageId);

        model.Requests.Should().BeEmpty();

        await using var read = _db.CreateContext();
        (await read.WhatsAppConversations.SingleAsync()).EscalationReason.Should().Be(WhatsAppBotReasons.HumanRequested);

        var turn = await read.WhatsAppBotTurns.SingleAsync();
        turn.Outcome.Should().Be(WhatsAppBotTurnOutcome.RuleEscalated);
        turn.Model.Should().Be("rules");
    }

    [Fact]
    public async Task Reaching_the_unanswered_threshold_hands_over_instead_of_sending_another_I_do_not_know()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true, maxTurns: 1);
        var model = new ScriptedModel(Tool(WhatsAppBotTools.SendReply, new { text = "Je ne sais pas.", answered_question = false }));

        var (conversationId, messageId) = await ReceiveAsync("Et pour un véhicule ?", model);
        await RunTurnAsync(model, conversationId, messageId);

        await using var read = _db.CreateContext();
        (await read.WhatsAppConversations.SingleAsync()).EscalationReason.Should().Be(WhatsAppBotReasons.NoUsefulAnswer);
        (await read.WhatsAppMessages.AnyAsync(m => m.Body == "Je ne sais pas.")).Should().BeFalse();
    }

    [Fact]
    public async Task The_bot_writes_only_the_fields_it_was_given_and_the_conversation_lists_them()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true, fields: ["CITY", "DESIRED_AMOUNT"]);
        var model = new ScriptedModel(
            (_, _) => Task.FromResult(new BotModelResponse(
            [
                new BotToolUseBlock("a", WhatsAppBotTools.UpdateLeadField, JsonSerializer.SerializeToElement(new { field = "CITY", value = "Thiès" })),
                new BotToolUseBlock("b", WhatsAppBotTools.UpdateLeadField, JsonSerializer.SerializeToElement(new { field = "EMAIL", value = "a@b.sn" })),
                new BotToolUseBlock("c", WhatsAppBotTools.UpdateLeadField, JsonSerializer.SerializeToElement(new { field = "DESIRED_AMOUNT", value = "2 millions" })),
            ], "tool_use", null, null)),
            Tool(WhatsAppBotTools.SendReply, new { text = "Merci, c’est noté.", answered_question = true }));

        var (conversationId, messageId) = await ReceiveAsync("J'habite à Thiès et il me faut 2 millions", model);
        await RunTurnAsync(model, conversationId, messageId);

        model.Requests[1].Messages[^1].Content.OfType<BotToolResultBlock>().Select(r => r.IsError).Should().Equal(false, true, false);

        await using var read = _db.CreateContext();
        var lead = await read.Leads.SingleAsync();
        lead.PrincipalCity.Should().Be("Thiès");
        lead.DesiredAmount!.Amount.Should().Be(2_000_000);
        lead.Email.Should().BeNull();

        (await read.WhatsAppConversations.SingleAsync()).BotCollectedFields.Should().Equal("CITY", "DESIRED_AMOUNT");
    }

    [Fact]
    public async Task A_redelivered_request_runs_nothing_twice_and_a_late_answer_is_dropped()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(Tool(WhatsAppBotTools.SendReply, new { text = "Réponse.", answered_question = true }));

        var (conversationId, first) = await ReceiveAsync("Première question", model);
        var (_, second) = await ReceiveAsync("Deuxième question", model, id: "wamid.2", minutes: 1);

        // The first message's turn sees a newer message and stays quiet: the second one answers both.
        await RunTurnAsync(model, conversationId, first);
        model.Requests.Should().BeEmpty();

        await RunTurnAsync(model, conversationId, second);
        await RunTurnAsync(model, conversationId, second);

        model.Requests.Should().HaveCount(1);
        model.Requests[0].Messages[^1].Content.OfType<BotTextBlock>().Single().Text.Should().Contain("Première question").And.Contain("Deuxième question");

        await using var read = _db.CreateContext();
        (await read.WhatsAppBotTurns.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Without_the_tenant_s_switch_the_customer_goes_straight_to_a_person()
    {
        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppBotEnabled, Arg.Any<CancellationToken>()).Returns(false);
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);

        await ReceiveAsync("Bonjour", new ScriptedModel());

        await using var read = _db.CreateContext();
        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.EscalationReason.Should().Be("CUSTOMER_REPLIED");
        await _publisher.DidNotReceive().PublishAsync(Arg.Any<WhatsAppBotTurnRequestedEvent>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task SeedLeadAsync()
    {
        await using var db = _db.CreateContext();

        var lead = Lead.Capture(
            tenantId: _tenantId, fullName: "Awa Ndiaye", phoneNumber: "+221771234567",
            source: LeadSource.Web, interestedProduct: "Crédit", preferredLanguage: "fr",
            location: new Sankore.Shared.Kernel.ValueObject.GeoPoint(14.7, -17.4),
            preferredAgencyId: null, clock: TimeProvider.System);

        db.Leads.Add(lead);
        db.LeadConsents.Add(LeadConsent.Create(
            _tenantId, lead.Id, ConsentType.WhatsAppContact, ConsentChannel.WebForm, _me, TimeProvider.System, "Formulaire"));

        await db.SaveChangesAsync();
    }

    private async Task SaveProfileAsync(Guid line, bool enabled, int maxTurns = 2, IReadOnlyList<string>? fields = null)
    {
        await using var db = _db.CreateContext();
        var version = await db.WhatsAppBotProfiles.Where(p => p.ProductLineId == line).CountAsync();

        db.WhatsAppBotProfiles.Add(WhatsAppBotProfile.Create(
            _tenantId, line, version + 1, enabled, "Bonjour, je suis l’assistant.", null, "Sois bref.",
            [new("Quels documents pour un crédit ?", "Une pièce d’identité et un justificatif de revenus.")],
            fields ?? [], null, null, WhatsAppBotProfile.DefaultKeywords, maxTurns, 120, _me, _now).Value!);

        await db.SaveChangesAsync();
    }

    private async Task<(Guid ConversationId, Guid MessageId)> ReceiveAsync(
        string text, IWhatsAppBotModel model, string id = "wamid.1", int minutes = 0)
    {
        await using (var db = _db.CreateContext())
        {
            var inbound = new InboundMessagingMessage(id, "+221771234567", "Awa", _now.AddMinutes(minutes - 5), "text", text, null, null, null, null);

            await new RecordMessagingEventsHandler(
                    db, _phoneIndexer, _blindIndexer, _customers, _settings, _realtime, _publisher,
                    new WhatsAppEscalation(db, _customers, _settings, _publisher, NullLogger<WhatsAppEscalation>.Instance),
                    new WhatsAppBotProfiles(db, _settings, model),
                    new WhatsAppAudit(Substitute.For<IAuditWriter>(), TimeProvider.System, NullLogger<WhatsAppAudit>.Instance),
                    new FixedClock(_now), NullLogger<RecordMessagingEventsHandler>.Instance)
                .Handle(new RecordMessagingEventsCommand(_tenantId, _connectionId, null, new MessagingEventBatch([inbound], [])), CancellationToken.None);
        }

        await using var read = _db.CreateContext();
        var message = await read.WhatsAppMessages.SingleAsync(m => m.ProviderMessageId == id);
        return (message.ConversationId, message.Id);
    }

    private async Task RunTurnAsync(IWhatsAppBotModel model, Guid conversationId, Guid messageId, int timeoutSeconds = 15)
    {
        await using var db = _db.CreateContext();

        var engine = new WhatsAppBotEngine(
            db,
            new WhatsAppBotProfiles(db, _settings, model),
            model,
            new WhatsAppEscalation(db, _customers, _settings, _publisher, NullLogger<WhatsAppEscalation>.Instance),
            _admin,
            _emailIndexer,
            _realtime,
            Options.Create(new WhatsAppBotOptions { TimeoutSeconds = timeoutSeconds }),
            new FixedClock(_now),
            NullLogger<WhatsAppBotEngine>.Instance);

        await engine.RunAsync(_tenantId, conversationId, messageId, CancellationToken.None);
    }
}
