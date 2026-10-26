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
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Features.WhatsApp.Complaints;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Realtime;
using Xunit;

/// <summary>
/// A complaint taken on WhatsApp (2026-10-26): question by question, no model — on a deployment
/// with NO bot model at all, which is the point — then recorded on the client's register.
/// </summary>
public sealed class WhatsAppComplaintIntakeTests : IDisposable
{
    private const string Key = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";
    private const string From = "+2250708091801";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private readonly TestDbContextFactory _db;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ILeadSettings _settings = Substitute.For<ILeadSettings>();
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IBlindIndexer _blindIndexer =
        new HmacBlindIndexer(Options.Create(new FieldProtectionOptions { SectionName = "Leads", FieldEncryptionKey = Key, BlindIndexKey = Key }));
    private readonly IPhoneBlindIndexer _phoneIndexer = new HmacPhoneBlindIndexer(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Leads:BlindIndexKey"] = Key }).Build());
    private int _sequence;

    public WhatsAppComplaintIntakeTests()
    {
        _db = new TestDbContextFactory(_tenantId);

        _customers.FindMatchesAsync(default, default, default, default, default).ReturnsForAnyArgs([]);
        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppBotComplaintsEnabled, Arg.Any<CancellationToken>()).Returns(true);

        // M01 normalises the number (spacing, casing); this module passes what the customer typed.
        _customers.IdentifyClientAsync(
                _tenantId,
                Arg.Is<string>(n => string.Equals(n.Trim(), "ABJ-2026-000001", StringComparison.OrdinalIgnoreCase)),
                new DateOnly(1985, 3, 7),
                Arg.Any<CancellationToken>())
            .Returns(_clientId);
        _customers.FileComplaintAsync(Arg.Any<FileClientComplaintRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new FiledClientComplaint(Guid.NewGuid(), "RCL-2026-000007", _now.AddDays(30))));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_complaint_is_taken_question_by_question_and_recorded_on_the_client_register()
    {
        await SayAsync("Bonjour, je veux faire une réclamation");
        await SayAsync("abj-2026-000001 ");
        await SayAsync("07/03/1985");
        await SayAsync("On m'a prélevé deux fois les frais de dossier le 12 octobre.");
        await SayAsync("3");
        await SayAsync("Oui");

        await _customers.Received(1).FileComplaintAsync(
            Arg.Is<FileClientComplaintRequest>(r =>
                r.TenantId == _tenantId
                && r.ClientId == _clientId
                && r.Category == "FeesAndPricing"
                && r.Subject == "Réclamation WhatsApp — Frais et tarifs"
                && r.Description == "On m'a prélevé deux fois les frais de dossier le 12 octobre."),
            Arg.Any<CancellationToken>());

        await using var db = _db.CreateContext();
        var conversation = await db.WhatsAppConversations.SingleAsync();
        conversation.ClientId.Should().Be(_clientId, "an unattached conversation is filed on the client it identified");
        conversation.Status.Should().Be(WhatsAppConversationStatus.Bot);

        var intake = await db.WhatsAppComplaintIntakes.SingleAsync();
        intake.Outcome.Should().Be(WhatsAppComplaintOutcome.Filed);
        intake.ComplaintReference.Should().Be("RCL-2026-000007");
        intake.Description.Should().BeNull("once in M01's register, this module holds no copy");
        intake.PendingClientNumber.Should().BeNull();

        (await db.WhatsAppMessages.Where(m => m.Direction == WhatsAppDirection.Inbound).ToListAsync())
            .Should().OnlyContain(m => m.ExcludedFromModel, "no model ever reads a client number, a birth date or a complaint");

        (await OutboundAsync(db)).Should().Contain(b => b.Contains("RCL-2026-000007") && b.Contains("25/11/2026"));
    }

    [Fact]
    public async Task Switched_off_the_word_still_hands_the_conversation_to_a_person()
    {
        _settings.GetBoolAsync(_tenantId, LeadSettingKeys.WhatsAppBotComplaintsEnabled, Arg.Any<CancellationToken>()).Returns(false);

        await SayAsync("Je veux porter plainte");

        await using var db = _db.CreateContext();
        (await db.WhatsAppConversations.SingleAsync()).Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        (await db.WhatsAppComplaintIntakes.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_date_that_cannot_be_read_is_asked_again_without_costing_a_try()
    {
        await SayAsync("réclamation");
        await SayAsync("ABJ-2026-000001");
        await SayAsync("le sept mars");

        await using var db = _db.CreateContext();
        var intake = await db.WhatsAppComplaintIntakes.SingleAsync();
        intake.Step.Should().Be(WhatsAppComplaintStep.BirthDate);
        intake.FailedIdentifications.Should().Be(0);
        (await OutboundAsync(db)).Should().Contain(WhatsAppComplaintDesk.DateNotUnderstood);
    }

    [Fact]
    public async Task Three_failed_identifications_in_a_day_hand_over_even_across_restarted_intakes()
    {
        await SayAsync("réclamation");
        await SayAsync("ABJ-9999");
        await SayAsync("01/01/1990");
        await SayAsync("ABJ-9998");
        await SayAsync("01/01/1990");
        await SayAsync("annuler");

        // Restarting must not buy fresh tries at somebody's number and date of birth.
        await SayAsync("réclamation");
        await SayAsync("ABJ-9997");
        await SayAsync("01/01/1990");

        await using var db = _db.CreateContext();
        var conversation = await db.WhatsAppConversations.SingleAsync();
        conversation.Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        conversation.EscalationReason.Should().Be(WhatsAppComplaintDesk.EscalationReason);
        conversation.ClientId.Should().BeNull();

        var intakes = await db.WhatsAppComplaintIntakes.OrderBy(i => i.StartedAt).ToListAsync();
        intakes.Select(i => i.Outcome).Should().Equal(WhatsAppComplaintOutcome.Cancelled, WhatsAppComplaintOutcome.Escalated);
        intakes[1].EndReason.Should().Be("IDENTIFICATION_FAILED");
        await _customers.DidNotReceiveWithAnyArgs().FileComplaintAsync(default!, default);
    }

    [Fact]
    public async Task A_register_that_refuses_the_complaint_hands_it_to_a_person_instead_of_losing_it()
    {
        _customers.FileComplaintAsync(Arg.Any<FileClientComplaintRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<FiledClientComplaint>("CLIENT_READ_ONLY"));

        await SayAsync("réclamation");
        await SayAsync("ABJ-2026-000001");
        await SayAsync("07/03/1985");
        await SayAsync("Ma carte est restée bloquée dans le distributeur.");
        await SayAsync("carte");
        await SayAsync("oui");

        await using var db = _db.CreateContext();
        (await db.WhatsAppConversations.SingleAsync()).Status.Should().Be(WhatsAppConversationStatus.PendingHuman);
        var intake = await db.WhatsAppComplaintIntakes.SingleAsync();
        intake.Outcome.Should().Be(WhatsAppComplaintOutcome.Escalated);
        intake.EndReason.Should().Be("CLIENT_READ_ONLY");
    }

    [Theory]
    [InlineData("07/03/1985", 1985, 3, 7)]
    [InlineData("7/3/1985", 1985, 3, 7)]
    [InlineData("07-03-1985", 1985, 3, 7)]
    [InlineData("1985-03-07", 1985, 3, 7)]
    [InlineData(" 07.03.1985 ", 1985, 3, 7)]
    public void A_birth_date_is_read_in_the_usual_numeric_forms(string text, int y, int m, int d)
        => WhatsAppComplaintDesk.ParseBirthDate(text, _now).Should().Be(new DateOnly(y, m, d));

    [Theory]
    [InlineData("07/03/2085")]
    [InlineData("31/02/1985")]
    [InlineData("hier")]
    public void A_date_in_the_future_or_impossible_is_not_a_birth_date(string text)
        => WhatsAppComplaintDesk.ParseBirthDate(text, _now).Should().BeNull();

    [Theory]
    [InlineData("1", "Credit")]
    [InlineData("4", "PaymentMeans")]
    [InlineData("mobile money", "PaymentMeans")]
    [InlineData("frais", "FeesAndPricing")]
    [InlineData("7", "Other")]
    public void A_category_is_picked_by_its_number_or_a_word(string normalized, string expected)
        => WhatsAppComplaintDesk.ParseCategory(normalized)!.Value.Category.Should().Be(expected);

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private async Task SayAsync(string text)
    {
        var id = $"wamid.{++_sequence}";
        var inbound = new InboundMessagingMessage(
            id, From, "Awa", _now.AddSeconds(_sequence), "text", text, null, null, null, null);

        await using var db = _db.CreateContext();
        var escalation = new WhatsAppEscalation(db, _customers, _settings, _publisher, NullLogger<WhatsAppEscalation>.Instance);

        await new RecordMessagingEventsHandler(
                db, _phoneIndexer, _blindIndexer, _customers, _settings, _realtime, _publisher,
                escalation,
                // No model on this deployment: the intake needs none.
                new WhatsAppBotProfiles(db, _settings, new UnavailableWhatsAppBotModel()),
                WhatsAppTestServices.Prospects(db, _phoneIndexer, _publisher, WhatsAppTestServices.FieldValues(_admin)),
                WhatsAppTestServices.FieldValues(_admin),
                new WhatsAppAudit(Substitute.For<IAuditWriter>(), TimeProvider.System, NullLogger<WhatsAppAudit>.Instance),
                new FixedClock(_now.AddSeconds(_sequence)), NullLogger<RecordMessagingEventsHandler>.Instance,
                new WhatsAppComplaintDesk(db, _customers, _settings, escalation, NullLogger<WhatsAppComplaintDesk>.Instance))
            .Handle(new RecordMessagingEventsCommand(_tenantId, _connectionId, null, new MessagingEventBatch([inbound], [])), CancellationToken.None);
    }

    private static async Task<List<string>> OutboundAsync(LeadsDbContext db) =>
        (await db.WhatsAppMessages.Where(m => m.Direction == WhatsAppDirection.Outbound).ToListAsync())
            .Select(m => m.Body ?? string.Empty)
            .ToList();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
