namespace Sankore.Modules.Customers.Tests.Features.Complaints;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Complaints;
using Sankore.Modules.Customers.Features.Duplicates;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.Features.Duplicates;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The client complaints register (2026-10-26): numbered per year, deadlines from the tenant's
/// settings, the description encrypted, assigned through M12's cascade and announced once.
/// </summary>
public sealed class ClientComplaintsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _advisor = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly CapturingPublisher _publisher = new();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly FixedClock _clock = new(Now);
    private readonly Guid _clientId;

    public ClientComplaintsTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency, advisorUserId: _advisor);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;

        // The advisor is eligible unless a test says otherwise.
        _administration.ResolveAgencyRecipientAsync(_tenant, _agency, _advisor, Arg.Any<CancellationToken>())
            .Returns(_advisor);
        _administration.ResolveAgencyRecipientAsync(_tenant, _agency, null, Arg.Any<CancellationToken>())
            .Returns(_manager);
    }

    public void Dispose() => _factory.Dispose();

    private ClientComplaintFiling Filing(CustomersDbContext db, params (string Key, string Value)[] settings) => new(
        db, TestDoubles.Encryptor(), TestDoubles.Settings(_tenant, settings), _publisher, _clock,
        NullLogger<ClientComplaintFiling>.Instance, _administration);

    private ClientComplaintHandlers Handlers(CustomersDbContext db, params (string Key, string Value)[] settings) => new(
        db, Filing(db, settings), TestDoubles.Encryptor(), TestDoubles.CurrentUser(_tenant, _actor),
        TestDoubles.AgencyScope(), _clock, _publisher);

    private async Task<Result<FileClientComplaintResponse>> RecordAsync(
        string description = "On m'a prélevé deux fois les frais de dossier.",
        Guid? holdingId = null,
        params (string Key, string Value)[] settings)
    {
        await using var db = _factory.CreateContext();
        return await Handlers(db, settings).Handle(new FileClientComplaintCommand(
            _clientId, ClientComplaintChannel.Branch, ClientComplaintCategory.FeesAndPricing,
            "Frais de dossier prélevés deux fois", description, holdingId), CancellationToken.None);
    }

    [Fact]
    public async Task A_complaint_is_numbered_dated_from_the_settings_encrypted_and_assigned_to_the_advisor()
    {
        var first = await RecordAsync();
        var second = await RecordAsync(settings: [(CustomerSettingKeys.ComplaintAcknowledgementDays, "5")]);

        first.Value!.Reference.Should().Be("RCL-2026-000001");
        second.Value!.Reference.Should().Be("RCL-2026-000002");

        await using var db = _factory.CreateContext();
        var row = await db.ClientComplaints.SingleAsync(c => c.Id == first.Value.ComplaintId);
        row.Status.Should().Be(ClientComplaintStatus.Received);
        row.ReceivedBy.Should().Be(_actor);
        row.AssignedTo.Should().Be(_advisor);
        row.AcknowledgementDueAt.Should().Be(Now.AddDays(2));
        row.ResolutionDueAt.Should().Be(Now.AddDays(30));
        row.BodyEncrypted.Should().StartWith("enc:", "what the client said is stored encrypted");

        (await db.ClientComplaints.SingleAsync(c => c.Id == second.Value.ComplaintId))
            .AcknowledgementDueAt.Should().Be(Now.AddDays(5));
    }

    [Fact]
    public async Task The_assignee_is_told_once_with_the_acknowledgement_deadline_and_never_the_description()
    {
        await RecordAsync();

        var evt = _publisher.Events.OfType<ClientComplaintActionRequiredEvent>().Should().ContainSingle().Subject;
        evt.RecipientUserId.Should().Be(_advisor);
        evt.Reason.Should().Be(ClientComplaintActionReasons.Filed);
        evt.DueAt.Should().Be(Now.AddDays(2));
        evt.Subject.Should().Be("Frais de dossier prélevés deux fois");
        evt.ToString().Should().NotContain("deux fois les frais", "the event carries the subject, not the client's words");
    }

    [Fact]
    public async Task An_advisor_M12_refuses_hands_the_complaint_to_the_agency_manager()
    {
        _administration.ResolveAgencyRecipientAsync(_tenant, _agency, _advisor, Arg.Any<CancellationToken>())
            .Returns(_manager);

        var id = (await RecordAsync()).Value!.ComplaintId;

        await using var db = _factory.CreateContext();
        (await db.ClientComplaints.SingleAsync(c => c.Id == id)).AssignedTo.Should().Be(_manager);
    }

    [Fact]
    public async Task M12_down_keeps_the_advisor_rather_than_losing_the_complaint()
    {
        _administration.ResolveAgencyRecipientAsync(_tenant, _agency, _advisor, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("down"));

        var id = (await RecordAsync()).Value!.ComplaintId;

        await using var db = _factory.CreateContext();
        (await db.ClientComplaints.SingleAsync(c => c.Id == id)).AssignedTo.Should().Be(_advisor);
    }

    [Fact]
    public async Task A_description_is_required_and_a_contract_must_be_the_clients()
    {
        (await RecordAsync(description: "  ")).Error.Should().Be(CustomerErrors.ComplaintDescriptionRequired);
        (await RecordAsync(holdingId: Guid.NewGuid())).Error.Should().Be(CustomerErrors.ComplaintHoldingNotOfClient);
    }

    [Fact]
    public async Task An_archived_client_takes_no_new_complaint()
    {
        await using (var db = _factory.CreateContext())
        {
            var client = await db.Clients.AsTracking().SingleAsync();
            client.Archive("Clôture", _actor).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }

        (await RecordAsync()).Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task An_answer_given_straight_away_also_acknowledges_and_is_read_back_decrypted()
    {
        var id = (await RecordAsync()).Value!.ComplaintId;

        await using (var db = _factory.CreateContext())
        {
            (await Handlers(db).Handle(new ResolveClientComplaintCommand(_clientId, id, " "), CancellationToken.None))
                .Error.Should().Be(CustomerErrors.ComplaintResponseRequired);
        }

        await using (var db = _factory.CreateContext())
        {
            (await Handlers(db).Handle(
                    new ResolveClientComplaintCommand(_clientId, id, "Les frais en double ont été remboursés."), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        await using (var db = _factory.CreateContext())
        {
            var detail = (await Handlers(db).Handle(new GetClientComplaintQuery(_clientId, id), CancellationToken.None)).Value!;
            detail.Summary.Status.Should().Be(nameof(ClientComplaintStatus.Resolved));
            detail.Summary.AcknowledgedAt.Should().Be(Now);
            detail.Description.Should().Be("On m'a prélevé deux fois les frais de dossier.");
            detail.Response.Should().Be("Les frais en double ont été remboursés.");

            (await db.ClientComplaints.SingleAsync()).ResponseEncrypted.Should().StartWith("enc:");
        }
    }

    [Fact]
    public async Task Each_step_is_announced_so_the_tasks_opened_for_it_can_close()
    {
        var id = (await RecordAsync()).Value!.ComplaintId;
        _publisher.Events.Clear();

        await using (var db = _factory.CreateContext())
            await Handlers(db).Handle(new AcknowledgeClientComplaintCommand(_clientId, id), CancellationToken.None);
        await using (var db = _factory.CreateContext())
            await Handlers(db).Handle(new ResolveClientComplaintCommand(_clientId, id, "Remboursé."), CancellationToken.None);

        _publisher.Events.OfType<ClientComplaintProgressedEvent>().Select(e => (e.ComplaintId, e.Stage))
            .Should().Equal((id, ClientComplaintStages.Acknowledged), (id, ClientComplaintStages.Resolved));
    }

    [Fact]
    public async Task A_refused_step_announces_nothing()
    {
        var id = (await RecordAsync()).Value!.ComplaintId;
        _publisher.Events.Clear();

        await using var db = _factory.CreateContext();
        (await Handlers(db).Handle(new ResolveClientComplaintCommand(_clientId, id, " "), CancellationToken.None)).IsFailure.Should().BeTrue();

        _publisher.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_closure_needs_a_reason_and_a_closed_complaint_moves_no_further()
    {
        var id = (await RecordAsync()).Value!.ComplaintId;

        await using (var db = _factory.CreateContext())
        {
            (await Handlers(db).Handle(new CloseClientComplaintCommand(_clientId, id, ""), CancellationToken.None))
                .Error.Should().Be(CustomerErrors.ReasonRequired);
        }

        await using (var db = _factory.CreateContext())
        {
            (await Handlers(db).Handle(new CloseClientComplaintCommand(_clientId, id, "Retirée par le client"), CancellationToken.None))
                .IsSuccess.Should().BeTrue();
        }

        await using (var db = _factory.CreateContext())
        {
            (await Handlers(db).Handle(new AcknowledgeClientComplaintCommand(_clientId, id), CancellationToken.None))
                .Error.Should().Be(CustomerErrors.ComplaintInvalidTransition);
        }
    }

    [Fact]
    public async Task A_late_acknowledgement_is_reported_once_to_the_manager_then_the_late_answer_once_more()
    {
        await RecordAsync();
        _publisher.Events.Clear();

        await using (var db = _factory.CreateContext())
        {
            (await ClientComplaintDeadlineJob.ReportAsync(db, _publisher, _administration, _tenant, Now.AddDays(1), CancellationToken.None))
                .Should().Be(0, "nothing is late yet");
            (await ClientComplaintDeadlineJob.ReportAsync(db, _publisher, _administration, _tenant, Now.AddDays(3), CancellationToken.None))
                .Should().Be(1);
            (await ClientComplaintDeadlineJob.ReportAsync(db, _publisher, _administration, _tenant, Now.AddDays(4), CancellationToken.None))
                .Should().Be(0, "a deadline is reported once, not every morning");
            (await ClientComplaintDeadlineJob.ReportAsync(db, _publisher, _administration, _tenant, Now.AddDays(31), CancellationToken.None))
                .Should().Be(1);
        }

        _publisher.Events.OfType<ClientComplaintActionRequiredEvent>().Select(e => (e.Reason, e.RecipientUserId))
            .Should().Equal(
                (ClientComplaintActionReasons.AcknowledgementOverdue, _manager),
                (ClientComplaintActionReasons.ResolutionOverdue, _manager));
    }

    [Fact]
    public async Task A_complaint_found_late_on_its_answer_is_one_reminder_not_two()
    {
        await RecordAsync();
        _publisher.Events.Clear();

        await using var db = _factory.CreateContext();
        (await ClientComplaintDeadlineJob.ReportAsync(db, _publisher, _administration, _tenant, Now.AddDays(40), CancellationToken.None))
            .Should().Be(1);
        (await ClientComplaintDeadlineJob.ReportAsync(db, _publisher, _administration, _tenant, Now.AddDays(41), CancellationToken.None))
            .Should().Be(0);

        _publisher.Events.OfType<ClientComplaintActionRequiredEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(ClientComplaintActionReasons.ResolutionOverdue);
    }

    [Fact]
    public async Task The_bot_files_as_SYSTEM_on_the_WhatsApp_channel_and_an_unknown_category_becomes_Other()
    {
        await using var db = _factory.CreateContext();
        var conversation = Guid.NewGuid();
        ICustomersModule facade = new CustomersModuleFacade(
            db, new UnusedLeadConversionService(), TestDoubles.Indexer(),
            TestHoldingSync.For(db, _tenant), TestDoubles.ClientEmails(db),
            TestDoubles.RemoteIdentity(db), TestDoubles.ReadIdentity(db), TestDoubles.ClientSms(db),
            complaintFiling: Filing(db));

        var filed = await facade.FileComplaintAsync(new FileClientComplaintRequest(
            _tenant, _clientId, "Weather", "Carte bloquée au guichet", "Ma carte est restée dans le GAB.",
            SourceConversationId: conversation), CancellationToken.None);

        filed.Value!.Reference.Should().Be("RCL-2026-000001");
        filed.Value.ResolutionDueAt.Should().Be(Now.AddDays(30));

        var row = await db.ClientComplaints.SingleAsync();
        row.Channel.Should().Be(ClientComplaintChannel.WhatsApp);
        row.Category.Should().Be(ClientComplaintCategory.Other);
        row.ReceivedBy.Should().Be(Guid.Empty);
        row.SourceConversationId.Should().Be(conversation);
        row.AssignedTo.Should().Be(_advisor, "a bot complaint is assigned and announced like any other");
    }

    [Fact]
    public async Task An_erased_client_keeps_its_complaints_counted_but_emptied()
    {
        var id = (await RecordAsync()).Value!.ComplaintId;

        await using (var db = _factory.CreateContext())
        {
            await ClientComplaintErasure.AnonymizeAsync(db, _tenant, _clientId, Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var row = await db.ClientComplaints.SingleAsync(c => c.Id == id);
            row.BodyEncrypted.Should().BeNull();
            row.Subject.Should().Be("Réclamation anonymisée");
            row.Category.Should().Be(ClientComplaintCategory.FeesAndPricing, "the register still counts it");
        }
    }

    private sealed class CapturingPublisher : IEventPublisher
    {
        public List<object> Events { get; } = [];

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct) where TEvent : IIntegrationEvent
        {
            Events.Add(@event!);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
