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
    private readonly MediatR.ISender _sender = Substitute.For<MediatR.ISender>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();

    private WhatsAppBotFieldValues FieldValues() =>
        WhatsAppTestServices.FieldValues(_admin, _emailIndexer, new FixedClock(_now));
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

    private static Func<BotModelRequest, CancellationToken, Task<BotModelResponse>> Respond(object input) =>
        (_, _) => Task.FromResult(new BotModelResponse(
            [new BotToolUseBlock($"toolu_{Guid.NewGuid():N}", WhatsAppBotTools.Respond, JsonSerializer.SerializeToElement(input))], "tool_use", 12, 7));

    private static Func<BotModelRequest, CancellationToken, Task<BotModelResponse>> Reply(string text, bool answered = true, object? collected = null) =>
        Respond(collected is null
            ? new { action = "reply", text, answered_question = answered }
            : new { action = "reply", text, answered_question = answered, collected });

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
    [InlineData("500 000 FCFA", "XOF", 500_000)]
    [InlineData("1,5 million", "XOF", 1_500_000)]
    [InlineData("750k", "XOF", 750_000)]
    [InlineData("2.000.000", "XOF", 2_000_000)]
    [InlineData("3000 euros", "EUR", 3_000)]
    [InlineData("250 000 FCFA", "XAF", 250_000)]
    public void Amounts_are_read_the_way_customers_type_them_in_the_institution_currency(
        string text, string institution, decimal amount)
    {
        var money = WhatsAppBotEngine.ParseAmount(text, institution);
        money!.Amount.Should().Be(amount);
        money.Currency.Should().Be(institution);
    }

    [Theory]
    [InlineData("3000 euros", "XOF")]
    [InlineData("500 000 FCFA", "EUR")]
    [InlineData("200 dollars", "XOF")]
    public void An_amount_named_in_another_currency_is_no_amount(string text, string institution)
        // Every amount of the institution is in its currency: the bot asks again rather than convert.
        => WhatsAppBotEngine.ParseAmount(text, institution).Should().BeNull();

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
        var handler = new SaveWhatsAppBotProfileHandler(db, _admin, FieldValues(), _currentUser, new FixedClock(_now));

        SaveWhatsAppBotProfileRequest Request(int expected, bool enabled) =>
            new(expected, enabled, "Bonjour", null, "Sois bref.", [new("Q ?", "R.")], ["EMAIL"], [new(1, "08:00", "17:00")],
                null, ["taux"], 2, 120);

        (await handler.Handle(new(Guid.Empty, Request(0, true)), CancellationToken.None)).Value!.Version.Should().Be(1);
        (await handler.Handle(new(Guid.Empty, Request(1, false)), CancellationToken.None)).Value!.Version.Should().Be(2);
        (await handler.Handle(new(Guid.Empty, Request(1, true)), CancellationToken.None)).Error.Should().Be(WhatsAppBotErrors.Conflict);

        var history = await new GetWhatsAppBotProfileHandler(db, _admin, FieldValues(), _currentUser)
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
        // The lead stores no product line: the product it ASKED for resolves to the line.
        _admin.ResolveProductLineAsync(_tenantId, "Crédit", Arg.Any<CancellationToken>()).Returns(line);
        var asked = Lead.Capture(_tenantId, "Awa", "+221771234567", LeadSource.Web, "Crédit", "fr",
            new Sankore.Shared.Kernel.ValueObject.GeoPoint(0, 0), null, TimeProvider.System);
        var unknown = Lead.Capture(_tenantId, "Awa", "+221771234567", LeadSource.Web, "Unknown", "fr",
            new Sankore.Shared.Kernel.ValueObject.GeoPoint(0, 0), null, TimeProvider.System);

        var profiles = new WhatsAppBotProfiles(db, _settings, new FakeWhatsAppBotModel(), _admin);
        (await profiles.ResolveActiveAsync(_tenantId, asked, CancellationToken.None)).Should().BeNull();
        (await profiles.ResolveActiveAsync(_tenantId, unknown, CancellationToken.None))!.IsGeneral.Should().BeTrue();
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
        var model = new ScriptedModel(Reply("Une pièce d’identité et un justificatif de revenus."));

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
        turn.InputTokens.Should().Be(12);
        turn.ReplyMessageId.Should().Be(outbound[1].Id);
        turn.Input.Should().Contain("[numéro]").And.NotContain("0708091801");

        model.Requests[0].Messages.SelectMany(m => m.Content).OfType<BotTextBlock>()
            .Should().NotContain(b => b.Text.Contains("0708091801"), "the number never leaves the platform");
        model.Requests.Should().ContainSingle("the FAQ is in the prompt: a question on the product is one call, not a lookup then an answer");
        model.Requests[0].System.Turn.Should().Contain("Bonjour, je suis l’assistant.", "the model is told the welcome already went");
        model.Requests[0].System.Stable.Should().Contain("Quels documents pour un crédit ?")
            .And.Contain("Une pièce d’identité et un justificatif de revenus.")
            .And.NotContain("Bonjour, je suis l’assistant.", "what changes every message stays out of the cached part");
        model.Requests[0].Tools.Select(t => t.Name).Should().Equal(WhatsAppBotTools.Respond);
    }

    [Fact]
    public async Task The_model_s_handover_is_saved_with_its_reason_and_summary()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(Respond(new { action = "handover", handover_reason = "ready_to_subscribe", handover_summary = "Veut ouvrir un compte épargne." }));

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
            Respond(new { action = "handover", handover_summary = "Un client." }),
            Respond(new { action = "handover", handover_reason = "complaint", handover_summary = "Se plaint d’un retard." }));

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
        var model = new ScriptedModel(Reply("x"));

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
        var model = new ScriptedModel(Reply("Je ne sais pas.", answered: false));

        var (conversationId, messageId) = await ReceiveAsync("Et pour un véhicule ?", model);
        await RunTurnAsync(model, conversationId, messageId);

        await using var read = _db.CreateContext();
        (await read.WhatsAppConversations.SingleAsync()).EscalationReason.Should().Be(WhatsAppBotReasons.NoUsefulAnswer);
        (await read.WhatsAppMessages.AnyAsync(m => m.Body == "Je ne sais pas.")).Should().BeFalse();
    }

    [Fact]
    public async Task The_bot_writes_only_the_fields_it_was_given_in_the_same_call_as_its_reply()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true, fields: ["CITY", "NEIGHBOURHOOD", "GENDER", "DESIRED_AMOUNT"]);
        var model = new ScriptedModel(Reply("Merci, c’est noté.", collected: new Dictionary<string, string>
        {
            ["CITY"] = "Thiès",
            ["NEIGHBOURHOOD"] = "Grand Thiès",
            ["GENDER"] = "Femme",
            ["EMAIL"] = "a@b.sn",
            ["DESIRED_AMOUNT"] = "2 millions",
        }));

        var (conversationId, messageId) = await ReceiveAsync("Je suis une femme de Grand Thiès, à Thiès, il me faut 2 millions", model);
        await RunTurnAsync(model, conversationId, messageId);

        model.Requests.Should().ContainSingle("the fields ride on the reply: no second call to answer after writing");

        await using var read = _db.CreateContext();
        var lead = await read.Leads.SingleAsync();
        lead.PrincipalCity.Should().Be("Thiès");
        lead.PrincipalNeighbourhood.Should().Be("Grand Thiès");
        lead.Gender.Should().Be(LeadGender.Female);
        lead.DesiredAmount!.Amount.Should().Be(2_000_000);
        lead.Email.Should().BeNull("EMAIL is not a field this profile collects");

        (await read.WhatsAppConversations.SingleAsync()).BotCollectedFields
            .Should().Equal("CITY", "NEIGHBOURHOOD", "GENDER", "DESIRED_AMOUNT");
        (await read.WhatsAppMessages.AnyAsync(m => m.Body == "Merci, c’est noté.")).Should().BeTrue();

        await _sender.Received(1).Send(
            Arg.Is<Sankore.Modules.Leads.Features.RecalculateLeadScore.RecalculateLeadScoreCommand>(c => c.LeadId == lead.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_field_the_engine_refuses_is_dropped_and_the_reply_still_goes_in_one_call()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true, fields: ["EMAIL", "GENDER"]);
        var model = new ScriptedModel(Reply("Merci.", collected: new Dictionary<string, string>
        {
            ["EMAIL"] = "pas-une-adresse",
            ["GENDER"] = "peut-être",
        }));

        var (conversationId, messageId) = await ReceiveAsync("Mon mail c'est pas-une-adresse", model);
        await RunTurnAsync(model, conversationId, messageId);

        model.Requests.Should().ContainSingle();

        await using var read = _db.CreateContext();
        (await read.WhatsAppMessages.AnyAsync(m => m.Body == "Merci.")).Should().BeTrue();
        (await read.WhatsAppConversations.SingleAsync()).BotCollectedFields.Should().BeEmpty();
        await _sender.DidNotReceiveWithAnyArgs().Send(default(Sankore.Modules.Leads.Features.RecalculateLeadScore.RecalculateLeadScoreCommand)!, default);
    }

    [Fact]
    public async Task The_product_is_chosen_from_the_active_catalogue_and_then_drives_the_profile()
    {
        var credit = new ProductSummary("CRD-COM", "Crédit commerçant", "Loan", true, Guid.NewGuid());
        var savings = new ProductSummary("EPG", "Épargne", "Savings", true, Guid.NewGuid());
        var retired = new ProductSummary("OLD", "Ancien produit", "Loan", false, Guid.NewGuid());
        _admin.ListProductsAsync(_tenantId, null, Arg.Any<CancellationToken>()).Returns([credit, savings, retired]);
        // M12's exact rule, on the name the bot records: what makes the next message use the
        // product's own profile now that the lead stores no line.
        _admin.ResolveProductLineAsync(_tenantId, "Crédit commerçant", Arg.Any<CancellationToken>()).Returns(credit.Id);

        await SeedLeadAsync(withProduct: false);
        await SaveProfileAsync(Guid.Empty, enabled: true, fields: ["PRODUCT"]);
        await SaveProfileAsync(credit.Id, enabled: true, faq: [new("Quel taux ?", "Un conseiller vous le précisera.")]);

        var model = new ScriptedModel(Reply("Très bien, le crédit commerçant.", collected: new { PRODUCT = "credit commercant" }));
        var (conversationId, messageId) = await ReceiveAsync("Je veux un crédit commerçant", model);
        await RunTurnAsync(model, conversationId, messageId);

        var offered = model.Requests[0].Tools.Single().InputSchema["properties"]!["collected"]!["properties"]!["PRODUCT"]!["enum"]!
            .AsArray().Select(n => n!.GetValue<string>());
        offered.Should().Equal("Crédit commerçant", "Épargne");
        model.Requests[0].System.Stable.Should().Contain("Crédit commerçant", "the general profile may name the catalogue");

        await using (var read = _db.CreateContext())
        {
            var lead = await read.Leads.SingleAsync();
            lead.InterestedProduct.Should().Be("Crédit commerçant", "the lead keeps what was asked, and no line");
        }

        // The next message is answered under the product's own profile, and the product is no
        // longer offered as something to record.
        var next = new ScriptedModel(Reply("Une pièce d’identité."));
        var (_, second) = await ReceiveAsync("Il faut quoi comme papiers ?", next, id: "wamid.2", minutes: 1);
        await RunTurnAsync(next, conversationId, second);

        next.Requests.Should().ContainSingle();
        next.Requests[0].System.Stable.Should().Contain("pour le produit « Crédit commerçant »").And.Contain("Quel taux ?");
        next.Requests[0].Tools.Single().InputSchema.ToJsonString().Should().NotContain("collected");
    }

    [Fact]
    public async Task A_product_the_catalogue_does_not_hold_is_not_recorded()
    {
        _admin.ListProductsAsync(_tenantId, null, Arg.Any<CancellationToken>())
            .Returns([new ProductSummary("EPG", "Épargne", "Savings", true, Guid.NewGuid())]);

        await SeedLeadAsync(withProduct: false);
        await SaveProfileAsync(Guid.Empty, enabled: true, fields: ["PRODUCT"]);
        var model = new ScriptedModel(Reply("D’accord.", collected: new { PRODUCT = "Crédit immobilier" }));

        var (conversationId, messageId) = await ReceiveAsync("Je veux un crédit immobilier", model);
        await RunTurnAsync(model, conversationId, messageId);

        await using var read = _db.CreateContext();
        (await read.Leads.SingleAsync()).InterestedProduct.Should().NotBe("Crédit immobilier");
        (await read.WhatsAppMessages.AnyAsync(m => m.Body == "D’accord.")).Should().BeTrue();
    }

    private sealed class CapturingHandler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task Anthropic_caches_the_stable_prompt_and_counts_what_the_cache_served()
    {
        var handler = new CapturingHandler("""
            {"content":[{"type":"tool_use","id":"toolu_1","name":"respond","input":{"action":"reply","text":"Oui.","answered_question":true}}],
             "stop_reason":"tool_use",
             "usage":{"input_tokens":40,"cache_read_input_tokens":1500,"cache_creation_input_tokens":0,"output_tokens":20}}
            """);
        var model = new AnthropicWhatsAppBotModel(new HttpClient(handler),
            Options.Create(new WhatsAppBotOptions { ApiKey = "sk-test", BaseUrl = "https://api.example.test/" }));

        var response = await model.CompleteAsync(new BotModelRequest(
            new BotSystemPrompt("Règles et FAQ.", "Informations à recueillir : …"),
            [new BotMessage("user", [new BotTextBlock("Bonjour")])],
            WhatsAppBotTools.For([], [], new BotFieldContext([], null)),
            []), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        var system = body.RootElement.GetProperty("system").EnumerateArray().ToList();

        system.Should().HaveCount(2);
        system[0].GetProperty("text").GetString().Should().Be("Règles et FAQ.");
        system[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        system[1].TryGetProperty("cache_control", out _).Should().BeFalse("the part that changes every message must not be the breakpoint");

        body.RootElement.GetProperty("tool_choice").GetProperty("type").GetString()
            .Should().Be("auto", "current models refuse a forced tool_choice with a 400");
        body.RootElement.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
        body.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("between_tools");

        response.InputTokens.Should().Be(1540, "what the cache served was still read by the model");
        response.OutputTokens.Should().Be(20);
    }

    [Fact]
    public async Task A_turn_answered_in_plain_text_is_sent_back_once_and_the_text_never_reaches_the_customer()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(
            (_, _) => Task.FromResult(new BotModelResponse([new BotTextBlock("Bonjour ! Comment puis-je aider ?")], "end_turn", 10, 5)),
            Reply("Bonjour, que puis-je pour vous ?"));

        var (conversationId, messageId) = await ReceiveAsync("Bonjour", model);
        await RunTurnAsync(model, conversationId, messageId);

        model.Requests.Should().HaveCount(2);
        model.Requests[1].Messages[^1].Content.OfType<BotTextBlock>().Single().Text.Should().Contain("respond");

        await using var read = _db.CreateContext();
        (await read.WhatsAppMessages.AnyAsync(m => m.Body == "Bonjour ! Comment puis-je aider ?")).Should().BeFalse();
        (await read.WhatsAppMessages.AnyAsync(m => m.Body == "Bonjour, que puis-je pour vous ?")).Should().BeTrue();
    }

    [Fact]
    public void A_block_the_engine_does_not_read_is_handed_back_unchanged()
    {
        using var doc = JsonDocument.Parse("""
            {"content":[{"type":"thinking","thinking":"","signature":"sig-123"},{"type":"tool_use","id":"t1","name":"respond","input":{"action":"reply","text":"Oui."}}]}
            """);

        var parsed = AnthropicWhatsAppBotModel.Parse(doc.RootElement);

        parsed.Content[0].Should().BeOfType<BotRawBlock>()
            .Which.Json.GetProperty("signature").GetString().Should().Be("sig-123");
    }

    [Fact]
    public void The_fake_model_answers_from_the_FAQ_in_one_call()
    {
        var request = new BotModelRequest(
            new BotSystemPrompt("règles", ""),
            [new BotMessage("user", [new BotTextBlock("Quels documents faut-il pour un crédit ?")])],
            [],
            [new("Quels documents pour un crédit ?", "Une pièce d’identité.")]);

        var response = new FakeWhatsAppBotModel().CompleteAsync(request, CancellationToken.None).Result;

        var use = response.Content.OfType<BotToolUseBlock>().Single();
        use.Name.Should().Be(WhatsAppBotTools.Respond);
        use.Input.GetProperty("text").GetString().Should().Be("Une pièce d’identité.");
    }

    [Fact]
    public async Task A_redelivered_request_runs_nothing_twice_and_a_late_answer_is_dropped()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true);
        var model = new ScriptedModel(Reply("Réponse."));

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

    // ── A prospect before any lead (lot 2) ─────────────────────────────────────

    private static readonly ProductSummary Credit = new("CRD-COM", "Crédit commerçant", "Loan", true, Guid.Parse("5a4d0b0e-1c9a-4f6b-9a7e-0c1d2e3f4a5b"));

    private async Task ProspectModeAsync(bool afterCollection = true)
    {
        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppCreateLeadForUnknownNumber, Arg.Any<CancellationToken>()).Returns(true);
        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppCreateLeadAfterCollection, Arg.Any<CancellationToken>()).Returns(afterCollection);
        _admin.ListProductsAsync(_tenantId, null, Arg.Any<CancellationToken>()).Returns([Credit]);
        await SaveProfileAsync(Guid.Empty, enabled: true,
            fields: ["FIRST_NAME", "LAST_NAME", "PRODUCT"], required: ["FIRST_NAME", "LAST_NAME", "PRODUCT"]);
    }

    [Fact]
    public async Task An_unknown_number_waits_as_a_prospect_while_the_bot_collects()
    {
        await ProspectModeAsync();
        var model = new ScriptedModel(Reply("Enchantée Awa, quel est votre nom de famille ?", collected: new { FIRST_NAME = "Awa" }));

        var (conversationId, messageId) = await ReceiveAsync("Bonjour, je m'appelle Awa", model);

        await using (var read = _db.CreateContext())
        {
            (await read.Leads.AnyAsync()).Should().BeFalse("a « bonjour » is not a lead yet");
            var conversation = await read.WhatsAppConversations.SingleAsync();
            conversation.LeadId.Should().BeNull();
            conversation.CollectingProspect.Should().BeTrue();
            conversation.Status.Should().Be(WhatsAppConversationStatus.Bot);
        }

        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<Sankore.Modules.Leads.Features.CaptureLead.Events.LeadCapturedEvent>(), Arg.Any<CancellationToken>());

        await RunTurnAsync(model, conversationId, messageId);

        model.Requests[0].System.Turn.Should().Contain("FIRST_NAME (Prénom) : manquant, indispensable");
        model.Requests[0].System.Stable.Should().Contain("ne pose plus de question");

        await using var after = _db.CreateContext();
        (await after.Leads.AnyAsync()).Should().BeFalse();
        var waiting = await after.WhatsAppConversations.SingleAsync();
        waiting.PendingLeadFields().Should().ContainKey("FIRST_NAME").WhoseValue.Should().Be("Awa");
        waiting.BotCollectedFields.Should().Equal("FIRST_NAME");
        (await after.WhatsAppMessages.AnyAsync(m => m.Body == "Enchantée Awa, quel est votre nom de famille ?")).Should().BeTrue();
    }

    [Fact]
    public async Task The_lead_is_created_once_the_minimum_is_collected_and_a_person_takes_over()
    {
        await ProspectModeAsync();

        var first = new ScriptedModel(Reply("Enchantée Awa, quel est votre nom de famille ?", collected: new { FIRST_NAME = "Awa" }));
        var (conversationId, m1) = await ReceiveAsync("Bonjour, je m'appelle Awa", first);
        await RunTurnAsync(first, conversationId, m1);

        var second = new ScriptedModel(Reply("Merci, un conseiller va vous recontacter.",
            collected: new { LAST_NAME = "Ndiaye", PRODUCT = "Crédit commerçant" }));
        var (_, m2) = await ReceiveAsync("Ndiaye, c'est pour un crédit commerçant", second, id: "wamid.2", minutes: 1);
        await RunTurnAsync(second, conversationId, m2);

        await using var read = _db.CreateContext();
        var lead = await read.Leads.SingleAsync();
        lead.FullName.Should().Be("Awa Ndiaye");
        lead.Source.Should().Be(LeadSource.WhatsApp);
        lead.InterestedProduct.Should().Be("Crédit commerçant");

        var consent = await read.LeadConsents.SingleAsync(c => c.LeadId == lead.Id);
        consent.Type.Should().Be(ConsentType.WhatsAppContact);
        consent.ProofReference.Should().Contain("wamid.1", "the customer asked to be talked to here with their FIRST message");

        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.LeadId.Should().Be(lead.Id);
        conversation.CollectingProspect.Should().BeFalse();
        conversation.PendingLeadFieldsJson.Should().BeNull("the values are on the lead now, not kept twice");
        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.EscalationReason.Should().Be(WhatsAppBotReasons.LeadReady);
        conversation.EscalationSummary.Should().Contain("Awa").And.Contain("Crédit commerçant");
        conversation.BotLeadReadyAt.Should().NotBeNull();

        var outbound = await read.WhatsAppMessages.Where(m => m.Direction == WhatsAppDirection.Outbound).OrderBy(m => m.CreatedAt).ToListAsync();
        outbound.Select(m => m.Body).TakeLast(2).Should().Equal("Merci, un conseiller va vous recontacter.", "Un conseiller arrive.");

        await _publisher.Received(1).PublishAsync(
            Arg.Is<Sankore.Modules.Leads.Features.CaptureLead.Events.LeadCapturedEvent>(e => e.LeadId == lead.Id), Arg.Any<CancellationToken>());
        await _sender.Received(1).Send(
            Arg.Is<Sankore.Modules.Leads.Features.RecalculateLeadScore.RecalculateLeadScoreCommand>(c => c.LeadId == lead.Id),
            Arg.Any<CancellationToken>());
        await _auditWriter.ReceivedWithAnyArgs(1).WriteAsync(default!, default);
    }

    [Fact]
    public async Task A_handover_before_the_minimum_creates_the_lead_with_what_was_collected()
    {
        await ProspectModeAsync();

        var first = new ScriptedModel(Reply("Enchantée Awa.", collected: new { FIRST_NAME = "Awa" }));
        var (conversationId, m1) = await ReceiveAsync("Je suis Awa", first);
        await RunTurnAsync(first, conversationId, m1);

        var second = new ScriptedModel(Respond(new { action = "handover", handover_reason = "complaint", handover_summary = "Se plaint d’un retard." }));
        var (_, m2) = await ReceiveAsync("Ça fait une semaine que j'attends", second, id: "wamid.2", minutes: 1);
        await RunTurnAsync(second, conversationId, m2);

        await using var read = _db.CreateContext();
        var lead = await read.Leads.SingleAsync();
        lead.FirstName.Should().Be("Awa");

        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.LeadId.Should().Be(lead.Id, "the person who takes over needs a record to work on");
        conversation.EscalationReason.Should().Be("BOT_COMPLAINT");
    }

    [Fact]
    public async Task A_rule_handover_creates_the_prospect_s_lead_too()
    {
        await ProspectModeAsync();
        var model = new ScriptedModel(Reply("x"));

        var (conversationId, messageId) = await ReceiveAsync("Je veux parler à un conseiller", model);
        await RunTurnAsync(model, conversationId, messageId);

        model.Requests.Should().BeEmpty();

        await using var read = _db.CreateContext();
        var lead = await read.Leads.SingleAsync();
        lead.FullName.Should().Be("Awa", "the WhatsApp profile name, as at the first message");
        (await read.WhatsAppConversations.SingleAsync()).LeadId.Should().Be(lead.Id);
    }

    [Fact]
    public async Task Without_a_bot_the_unknown_number_becomes_a_lead_at_once_whatever_the_setting()
    {
        await ProspectModeAsync();
        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppBotEnabled, Arg.Any<CancellationToken>()).Returns(false);

        await ReceiveAsync("Bonjour", new ScriptedModel());

        await using var read = _db.CreateContext();
        var lead = await read.Leads.SingleAsync();
        var conversation = await read.WhatsAppConversations.SingleAsync();
        conversation.LeadId.Should().Be(lead.Id);
        conversation.CollectingProspect.Should().BeFalse();
    }

    [Fact]
    public async Task With_the_setting_off_the_unknown_number_becomes_a_lead_at_the_first_message()
    {
        await ProspectModeAsync(afterCollection: false);

        await ReceiveAsync("Bonjour", new ScriptedModel());

        await using var read = _db.CreateContext();
        (await read.WhatsAppConversations.SingleAsync()).LeadId.Should().Be((await read.Leads.SingleAsync()).Id);
    }

    [Fact]
    public async Task On_a_lead_the_bot_hands_over_only_when_its_own_collection_completes_the_minimum()
    {
        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true, fields: ["FIRST_NAME", "CITY"], required: ["CITY"]);

        var first = new ScriptedModel(Reply("Enchantée.", collected: new { FIRST_NAME = "Awa" }));
        var (conversationId, m1) = await ReceiveAsync("Je suis Awa", first);
        await RunTurnAsync(first, conversationId, m1);

        await using (var read = _db.CreateContext())
            (await read.WhatsAppConversations.SingleAsync()).Status.Should().Be(WhatsAppConversationStatus.Bot,
                "FIRST_NAME is not part of the minimum");

        var second = new ScriptedModel(Reply("Merci.", collected: new { CITY = "Thiès" }));
        var (_, m2) = await ReceiveAsync("J'habite Thiès", second, id: "wamid.2", minutes: 1);
        await RunTurnAsync(second, conversationId, m2);

        await using var after = _db.CreateContext();
        var conversation = await after.WhatsAppConversations.SingleAsync();
        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.EscalationReason.Should().Be(WhatsAppBotReasons.LeadReady);
        (await after.Leads.CountAsync()).Should().Be(1, "the lead existed; nothing is created twice");
    }

    [Fact]
    public void A_profile_requires_only_what_it_collects_and_keeps_a_custom_key_as_written()
    {
        Result<WhatsAppBotProfile> Create(IReadOnlyList<string> fields, IReadOnlyList<string> required) =>
            WhatsAppBotProfile.Create(_tenantId, Guid.Empty, 1, true, null, null, null, null, fields, null, null, null, 2, 120, _me, _now, required);

        Create(["FIRST_NAME"], ["CITY"]).Error.Should().Be("WHATSAPP_BOT_REQUIRED_NOT_COLLECTED");

        var profile = Create(["first_name", "Custom.segmentClient"], ["custom.segmentClient"]).Value!;
        profile.FieldsToCollect.Should().Equal("FIRST_NAME", "custom.segmentClient");
        profile.FieldsRequiredForLead.Should().Equal("custom.segmentClient");
    }

    private static Sankore.Modules.Administration.PublicApi.Forms.EntityFormDto LeadForm() => new(
        Sankore.Modules.Administration.PublicApi.Forms.FormEntity.Lead, 1, [],
        [
            new("segment", Sankore.Modules.Administration.PublicApi.Forms.FormFieldType.SingleChoice, "Segment", 1,
                Options: [new("Commercant", "Commerçant"), new("Salarie", "Salarié")]),
            new("revenu", Sankore.Modules.Administration.PublicApi.Forms.FormFieldType.Text, "Revenu déclaré", 2, IsSensitive: true),
        ]);

    [Fact]
    public async Task A_custom_field_of_the_lead_form_is_offered_with_its_options_and_written_through_the_form()
    {
        _admin.GetEntityFormAsync(_tenantId, Sankore.Modules.Administration.PublicApi.Forms.FormEntity.Lead, Arg.Any<CancellationToken>())
            .Returns(LeadForm());

        await SeedLeadAsync();
        await SaveProfileAsync(Guid.Empty, enabled: true, fields: ["custom.segment"]);
        var model = new ScriptedModel(Reply("Merci.", collected: new Dictionary<string, string> { ["custom.segment"] = "Commerçant" }));

        var (conversationId, messageId) = await ReceiveAsync("Je suis commerçante", model);
        await RunTurnAsync(model, conversationId, messageId);

        var options = model.Requests[0].Tools.Single().InputSchema["properties"]!["collected"]!["properties"]!["custom.segment"]!["enum"]!
            .AsArray().Select(n => n!.GetValue<string>());
        options.Should().Equal("Commerçant", "Salarié");
        model.Requests[0].System.Turn.Should().Contain("custom.segment (Segment) : manquant");

        await using var read = _db.CreateContext();
        (await read.Leads.SingleAsync()).CustomFieldsJson.Should().Contain("Commercant", "the form stores the option's value");
        (await read.WhatsAppConversations.SingleAsync()).BotCollectedFields.Should().Equal("custom.segment");
    }

    [Fact]
    public async Task A_sensitive_custom_field_is_neither_offered_nor_accepted_in_a_profile()
    {
        _admin.GetEntityFormAsync(_tenantId, Sankore.Modules.Administration.PublicApi.Forms.FormEntity.Lead, Arg.Any<CancellationToken>())
            .Returns(LeadForm());

        GetWhatsAppBotOverviewHandler.Fields(LeadForm()).Select(f => f.Code)
            .Should().Contain("custom.segment").And.NotContain("custom.revenu");

        await using var db = _db.CreateContext();
        var saved = await new SaveWhatsAppBotProfileHandler(db, _admin, FieldValues(), _currentUser, new FixedClock(_now))
            .Handle(new(Guid.Empty, new SaveWhatsAppBotProfileRequest(0, true, null, null, null, null, ["custom.revenu"], null, null, null, 2, 120)),
                CancellationToken.None);

        saved.Error.Should().Be("WHATSAPP_BOT_FIELD_UNKNOWN");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task SeedLeadAsync(bool withProduct = true)
    {
        await using var db = _db.CreateContext();

        var lead = Lead.Capture(
            tenantId: _tenantId, fullName: "Awa Ndiaye", phoneNumber: "+221771234567",
            source: LeadSource.Web, interestedProduct: withProduct ? "Crédit" : "Unknown", preferredLanguage: "fr",
            location: new Sankore.Shared.Kernel.ValueObject.GeoPoint(14.7, -17.4),
            preferredAgencyId: null, clock: TimeProvider.System);

        db.Leads.Add(lead);
        db.LeadConsents.Add(LeadConsent.Create(
            _tenantId, lead.Id, ConsentType.WhatsAppContact, ConsentChannel.WebForm, _me, TimeProvider.System, "Formulaire"));

        await db.SaveChangesAsync();
    }

    private async Task SaveProfileAsync(
        Guid line, bool enabled, int maxTurns = 2, IReadOnlyList<string>? fields = null, IReadOnlyList<WhatsAppBotFaqEntry>? faq = null,
        IReadOnlyList<string>? required = null)
    {
        await using var db = _db.CreateContext();
        var version = await db.WhatsAppBotProfiles.Where(p => p.ProductLineId == line).CountAsync();

        db.WhatsAppBotProfiles.Add(WhatsAppBotProfile.Create(
            _tenantId, line, version + 1, enabled, "Bonjour, je suis l’assistant.", null, "Sois bref.",
            faq ?? [new("Quels documents pour un crédit ?", "Une pièce d’identité et un justificatif de revenus.")],
            fields ?? [], null, null, WhatsAppBotProfile.DefaultKeywords, maxTurns, 120, _me, _now, required).Value!);

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
                    new WhatsAppBotProfiles(db, _settings, model, _admin),
                    WhatsAppTestServices.Prospects(db, _phoneIndexer, _publisher, FieldValues(), _integration),
                    FieldValues(),
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
            new WhatsAppBotProfiles(db, _settings, model, _admin),
            model,
            new WhatsAppEscalation(db, _customers, _settings, _publisher, NullLogger<WhatsAppEscalation>.Instance),
            FieldValues(),
            WhatsAppTestServices.Prospects(db, _phoneIndexer, _publisher, FieldValues(), _integration),
            new WhatsAppAudit(_auditWriter, TimeProvider.System, NullLogger<WhatsAppAudit>.Instance),
            _realtime,
            Options.Create(new WhatsAppBotOptions { TimeoutSeconds = timeoutSeconds }),
            _sender,
            new FixedClock(_now),
            NullLogger<WhatsAppBotEngine>.Instance);

        await engine.RunAsync(_tenantId, conversationId, messageId, CancellationToken.None);
    }
}
