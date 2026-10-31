namespace Sankore.Modules.Leads.Tests.Features.WhatsApp;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Complaints lot 1 (2026-10-31): an advisor writes to a CLIENT on WhatsApp — a template, then a
/// free reply inside the window — and each message sent from a complaint names it. No consent is
/// asked (a service relationship), but a STOP, a processing restriction and the client's perimeter
/// are respected, and a complaint of another client cannot be named.
/// </summary>
public sealed class ClientWhatsAppTests : IDisposable
{
    private const string Key = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _complaint = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private readonly TestDbContextFactory _factory;
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly IMessagingGateway _messaging = Substitute.For<IMessagingGateway>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IBlindIndexer _indexer =
        new HmacBlindIndexer(Options.Create(new FieldProtectionOptions { SectionName = "Leads", FieldEncryptionKey = Key, BlindIndexKey = Key }));

    public ClientWhatsAppTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _user.Id.Returns(_me);
        _user.TenantId.Returns(_tenant);

        _customers.GetClientSummaryAsync(_tenant, _client, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(_client, "CLI-001", "Individual", "Awa Koné", "Active", Guid.NewGuid(), _me, "Approved", "Low", null));
        _customers.CanActorAccessClientAsync(_tenant, _client, _me, Arg.Any<CancellationToken>()).Returns(true);
        _customers.ResolvePhoneForCallAsync(_tenant, _client, PhoneSlot.Primary, _me, Arg.Any<CancellationToken>())
            .Returns(Result.Ok("0708091801"));
        _customers.GetComplaintReferenceAsync(_tenant, _client, _complaint, Arg.Any<CancellationToken>())
            .Returns(new ClientComplaintReference(_complaint, "RCL-2026-000042", true));

        _integration.Messaging.Returns(_messaging);
        _messaging.ResolveChannelAsync(_tenant, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new MessagingChannelInfo(_connection, "Numéro principal", "Dialog360", "+2250700000000", null, true));
        _admin.GetSmsConfigAsync(_tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto("Disabled", null, null, "225", false, false, null, 0));
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ClientWhatsAppContext Context(LeadsDbContext db) => new(db, _user, _customers, _admin, _integration, _indexer);

    private async Task<WhatsAppTemplate> TemplateAsync()
    {
        await using var db = _factory.CreateContext();
        var template = WhatsAppTemplate.Discover(_tenant, _connection, "suivi_reclamation", "fr", _now);
        template.Refresh("UTILITY", "APPROVED", null, null, "Bonjour {{1}}, au sujet de votre réclamation {{2}}.", null, [], _now);
        template.Configure([], [new WhatsAppVariableMapping(1, WhatsAppTemplateField.LeadFirstName)], _me, _now).IsSuccess.Should().BeTrue();
        db.WhatsAppTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    private async Task<Result<SendWhatsAppTemplateResult>> SendTemplateAsync(Guid templateId, Guid? complaintId)
    {
        await using var db = _factory.CreateContext();
        return await new SendClientWhatsAppTemplateHandler(db, Context(db), _user, _publisher, new FixedClock(_now))
            .Handle(new SendClientWhatsAppTemplateCommand(_client, templateId, ["Awa", "RCL-2026-000042"], complaintId, MayForce: false), default);
    }

    [Fact]
    public async Task The_panel_offers_the_template_with_the_clients_first_name_filled_in()
    {
        await TemplateAsync();
        await using var db = _factory.CreateContext();

        var panel = await new GetClientWhatsAppHandler(db, Context(db), _user, _admin, new FixedClock(_now))
            .Handle(new GetClientWhatsAppQuery(_client, MayForce: false), default);

        panel.Value!.Blockers.Should().BeEmpty("no consent is asked to follow up a complaint");
        panel.Value.MaskedPhone.Should().NotContain("0708091801");
        panel.Value.Templates.Single().Variables.First().Value.Should().Be("Awa");
    }

    [Fact]
    public async Task A_template_opens_a_conversation_on_the_client_and_names_the_complaint()
    {
        var template = await TemplateAsync();

        var sent = await SendTemplateAsync(template.Id, _complaint);

        sent.IsSuccess.Should().BeTrue(sent.Error);
        await using var db = _factory.CreateContext();
        var conversation = await db.WhatsAppConversations.SingleAsync();
        conversation.ClientId.Should().Be(_client);
        conversation.LeadId.Should().BeNull();
        (await db.WhatsAppMessages.SingleAsync()).ComplaintId.Should().Be(_complaint);
        await _publisher.Received(1).PublishAsync(Arg.Any<WhatsAppMessageQueuedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_complaint_of_another_client_cannot_be_named()
    {
        var template = await TemplateAsync();

        (await SendTemplateAsync(template.Id, Guid.NewGuid())).Error.Should().Be(ClientWhatsAppErrors.ComplaintNotFound);
    }

    [Fact]
    public async Task A_client_outside_the_advisors_perimeter_is_not_found()
    {
        var template = await TemplateAsync();
        _customers.CanActorAccessClientAsync(_tenant, _client, _me, Arg.Any<CancellationToken>()).Returns(false);

        (await SendTemplateAsync(template.Id, null)).Error.Should().Be(ClientWhatsAppErrors.ClientNotFound);
    }

    [Fact]
    public async Task A_restricted_client_is_not_written_to()
    {
        var template = await TemplateAsync();
        _customers.IsProcessingRestrictedAsync(_tenant, _client, Arg.Any<CancellationToken>()).Returns(true);

        (await SendTemplateAsync(template.Id, null)).Error.Should().Be(WhatsAppErrors.ProcessingRestricted);
    }

    [Fact]
    public async Task A_client_who_wrote_STOP_is_not_written_to_again()
    {
        var template = await TemplateAsync();
        (await SendTemplateAsync(template.Id, null)).IsSuccess.Should().BeTrue();
        await using (var db = _factory.CreateContext())
        {
            var conversation = await db.WhatsAppConversations.AsTracking().SingleAsync();
            conversation.RecordOptOut(_now);
            await db.SaveChangesAsync();
        }

        (await SendTemplateAsync(template.Id, null)).Error.Should().Be(ClientWhatsAppErrors.OptedOut);
    }

    [Fact]
    public async Task A_free_reply_needs_the_conversation_held_and_the_window_open()
    {
        var template = await TemplateAsync();
        (await SendTemplateAsync(template.Id, _complaint)).IsSuccess.Should().BeTrue();

        async Task<Result<Guid>> ReplyAsync()
        {
            await using var db = _factory.CreateContext();
            return await new SendClientWhatsAppTextHandler(db, Context(db), _user, _publisher, new FixedClock(_now.AddMinutes(10)))
                .Handle(new SendClientWhatsAppTextCommand(_client, "Votre dossier est en cours.", _complaint), default);
        }

        (await ReplyAsync()).Error.Should().Be(WhatsAppConversationErrors.NotHolder);

        await using (var db = _factory.CreateContext())
        {
            var conversation = await db.WhatsAppConversations.AsTracking().SingleAsync();
            conversation.RecordInbound(_now.AddMinutes(5), "Awa");   // the client answers: the window opens
            conversation.TakeOver(_me, _now.AddMinutes(6)).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        var replied = await ReplyAsync();
        replied.IsSuccess.Should().BeTrue(replied.Error);
        await using var verify = _factory.CreateContext();
        (await verify.WhatsAppMessages.SingleAsync(m => m.Id == replied.Value)).ComplaintId.Should().Be(_complaint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_number_whose_conversation_is_a_leads_or_another_clients_is_neither_read_nor_written(bool onLead)
    {
        // A family phone: the number's single conversation belongs to another record.
        var template = await TemplateAsync();
        await using (var db = _factory.CreateContext())
        {
            var e164 = "+2250708091801";
            var conversation = WhatsAppConversation.Open(_tenant, _connection, e164,
                _indexer.Compute(BlindIndexPurpose.Phone, e164),
                leadId: onLead ? Guid.NewGuid() : null, clientId: onLead ? null : Guid.NewGuid(),
                contactName: null, ambiguousMatch: false, _now.AddDays(-1));
            db.WhatsAppConversations.Add(conversation);
            db.WhatsAppMessages.Add(WhatsAppMessage.OutboundText(_tenant, conversation.Id, WhatsAppAuthor.Agent, Guid.NewGuid(), "Message d'un autre dossier", _now.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var panel = await new GetClientWhatsAppHandler(db, Context(db), _user, _admin, new FixedClock(_now))
                .Handle(new GetClientWhatsAppQuery(_client, MayForce: false), default);
            panel.Value!.ConversationId.Should().BeNull();
            panel.Value.Blockers.Should().Contain(ClientWhatsAppErrors.NumberElsewhere);

            var messages = await new ListClientWhatsAppMessagesHandler(db, Context(db), _user, _admin)
                .Handle(new ListClientWhatsAppMessagesQuery(_client, null, null, 30), default);
            messages.Value!.Items.Should().BeEmpty();

            (await new TakeOverClientWhatsAppHandler(db, Context(db), _user, new FixedClock(_now))
                .Handle(new TakeOverClientWhatsAppCommand(_client), default)).Error.Should().Be(ClientWhatsAppErrors.NoConversation);
        }

        (await SendTemplateAsync(template.Id, null)).Error.Should().Be(ClientWhatsAppErrors.NumberElsewhere);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "WHATSAPP_TEMPLATE_COOLDOWN")]
    public async Task Only_a_template_that_reached_the_provider_blocks_the_next_one_for_24_hours(bool refused, string? expected)
    {
        // A template refused (here by the test mode) never reached the client: it is not « insisting ».
        var template = await TemplateAsync();
        (await SendTemplateAsync(template.Id, _complaint)).IsSuccess.Should().BeTrue();
        await using (var db = _factory.CreateContext())
        {
            var message = await db.WhatsAppMessages.AsTracking().SingleAsync();
            message.TryBeginSending().Should().BeTrue();
            if (refused) message.MarkFailed("MESSAGING_TEST_RECIPIENT", null, _now);
            else message.MarkSent("wamid.1", _now);
            await db.SaveChangesAsync();
        }

        var again = await SendTemplateAsync(template.Id, _complaint);

        again.Error.Should().Be(expected);
    }
}
