namespace Sankore.Modules.Leads.Tests.Features.WhatsApp;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Opportunities.ProjectFields;
using Sankore.Modules.Leads.Features.Opportunities.Sending;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Features.WhatsApp;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Realtime;
using Xunit;

/// <summary>
/// Project documents lot 2 (2026-11-08): a project's devis or proposal sent by WhatsApp from its
/// page, inside the 24-hour window — to its lead, else its client — under the same rules as the
/// record's own WhatsApp tab; and an image or a PDF sent to a client, which only text could be before.
/// </summary>
public sealed class ProjectDocumentsWhatsAppTests : IDisposable
{
    private const string Key = "bGVhZHMtdGVzdHMtb25seS1ub3QtYS1zZWNyZXQtMzI=";
    private const string Phone = "+221771234567";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly TestDbContextFactory _db;
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly IMessagingGateway _messaging = Substitute.For<IMessagingGateway>();
    private readonly MemoryStore _store = new();
    private readonly MediaStore _media = new();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IBlindIndexer _indexer =
        new HmacBlindIndexer(Options.Create(new FieldProtectionOptions { SectionName = "Leads", FieldEncryptionKey = Key, BlindIndexKey = Key }));

    public ProjectDocumentsWhatsAppTests()
    {
        _db = new TestDbContextFactory(_tenant);
        _user.Id.Returns(_me);
        _user.TenantId.Returns(_tenant);
        _user.IsAuthenticated.Returns(true);
        _user.Roles.Returns(["Agent"]);
        _user.Permissions.Returns([]);

        _admin.GetSmsConfigAsync(_tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto("Disabled", null, null, "221", false, false, null, 0));
        _admin.GetUserSupervisorsAsync(_tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(UserSupervisors.None);
        _admin.GetTeamAgentIdsAsync(_tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        _integration.Messaging.Returns(_messaging);
        _messaging.ResolveChannelAsync(_tenant, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new MessagingChannelInfo(_connection, "Numéro principal", "Dialog360", "+2210000000", null, true));
        var sent = 0;
        _messaging.SendMediaAsync(_tenant, _connection, Arg.Any<string>(), Arg.Any<MessagingMediaContent>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => IntegrationResult.Ok(new MessagingMediaSent($"wamid.{++sent}", $"media-{sent}")));
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryStore : ITaskAttachmentStore
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public Task<StoredTaskAttachment> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct)
        {
            var reference = Guid.NewGuid().ToString("N");
            _files[reference] = content;
            return Task.FromResult(new StoredTaskAttachment(reference, content.Length, "sha"));
        }

        public Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct) => Task.FromResult(_files.GetValueOrDefault(storageRef));

        public Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct) => Task.FromResult(_files.Remove(storageRef));
    }

    private sealed class MediaStore : Sankore.Modules.Leads.Infrastructure.WhatsAppMedia.IWhatsAppMediaStore
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public Task<Sankore.Modules.Leads.Infrastructure.WhatsAppMedia.StoredWhatsAppMedia> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct)
        {
            var reference = "wam-" + Guid.NewGuid().ToString("N");
            Files[reference] = content;
            return Task.FromResult(new Sankore.Modules.Leads.Infrastructure.WhatsAppMedia.StoredWhatsAppMedia(reference, content.Length, "sha"));
        }

        public Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct) => Task.FromResult(Files.GetValueOrDefault(storageRef));

        public Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct) => Task.FromResult(Files.Remove(storageRef));
    }

    private sealed class Services(params (Type Type, object Service)[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.FirstOrDefault(s => s.Type == serviceType).Service;
    }

    // ── Wiring ─────────────────────────────────────────────────────────────────

    private ClientWhatsAppContext ClientContext(LeadsDbContext db) => new(db, _user, _customers, _admin, _integration, _indexer);

    private ProjectWhatsAppTarget Target(LeadsDbContext db) => new(
        db, _user, new LeadVisibility(_user), new WhatsAppLeadAccess(db, new LeadAccessGuard(_user, _admin)), _admin, _integration,
        _indexer, new Services((typeof(ClientWhatsAppContext), ClientContext(db))), new FixedClock(_now), _customers);

    private WhatsAppMediaSender Sender(LeadsDbContext db) =>
        new(db, _user, _integration, new FixedClock(_now), new Services(), NullLogger<WhatsAppMediaSender>.Instance);

    private ProjectSendingReader Reader(LeadsDbContext db) => new(
        db, _user, new LeadVisibility(_user), new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenant)),
        new Services((typeof(ITaskAttachmentStore), _store)));

    private async Task<Result<ProjectWhatsAppDto>> PanelAsync(Guid projectId)
    {
        await using var db = _db.CreateContext();
        return await new GetProjectWhatsAppHandler(Target(db), _user, new FixedClock(_now)).Handle(new GetProjectWhatsAppQuery(projectId), default);
    }

    private async Task<Result<IReadOnlyList<ProjectWhatsAppSentDto>>> SendAsync(Guid projectId, string? caption, params Guid[] files)
    {
        await using var db = _db.CreateContext();
        return await new SendProjectWhatsAppDocumentsHandler(Target(db), Reader(db), Sender(db), _user, new ProjectDocumentTrail(db, new FixedClock(_now)), db)
            .Handle(new SendProjectWhatsAppDocumentsCommand(projectId, files, caption), default);
    }

    // ── Seeds ──────────────────────────────────────────────────────────────────

    private async Task<Lead> LeadAsync(Guid owner, bool consent = true)
    {
        await using var db = _db.CreateContext();
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Awa Ndiaye", phoneNumber: Phone, source: LeadSource.Web, interestedProduct: "Crédit",
            preferredLanguage: "fr", location: new Sankore.Shared.Kernel.ValueObject.GeoPoint(14.7, -17.4),
            preferredAgencyId: null, clock: TimeProvider.System, ownerId: owner, agencyId: null);
        db.Leads.Add(lead);
        if (consent)
        {
            db.LeadConsents.Add(LeadConsent.Create(
                _tenant, lead.Id, ConsentType.WhatsAppContact, ConsentChannel.WebForm, _me, TimeProvider.System, "Formulaire"));
        }
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<WhatsAppConversation> ConversationAsync(Guid? leadId, Guid? clientId, Guid? holder, bool windowOpen = true)
    {
        await using var db = _db.CreateContext();
        var conversation = WhatsAppConversation.Open(
            _tenant, _connection, Phone, _indexer.Compute(BlindIndexPurpose.Phone, Phone), leadId, clientId, "Awa", false, _now.AddDays(-2));
        conversation.RecordInbound(windowOpen ? _now.AddHours(-1) : _now.AddDays(-2), "Awa");
        if (holder is { } h) conversation.TakeOver(h, _now.AddMinutes(-30));
        db.WhatsAppConversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private async Task<Opportunity> LeadProjectAsync(Guid leadId, Guid? owner = null)
    {
        await using var db = _db.CreateContext();
        var project = Opportunity.CreateFromLead(_tenant, leadId, "Crédit tracteur", "Crédit de campagne", owner, TimeProvider.System);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    private async Task<ProjectAttachment> FileAsync(Guid projectId, string name, string contentType, byte[]? content = null)
    {
        var bytes = content ?? Encoding.UTF8.GetBytes("%PDF-1.7 " + name);
        var stored = await _store.StoreAsync(_tenant, bytes, default);
        var row = ProjectAttachment.Record(_tenant, projectId, "devis", name, contentType, stored.SizeBytes, stored.Sha256, stored.StorageRef, _me, _now);
        await using var db = _db.CreateContext();
        db.ProjectAttachments.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // ── To the project's lead ──────────────────────────────────────────────────

    [Fact]
    public async Task The_panel_names_the_lead_its_conversation_and_which_files_WhatsApp_carries()
    {
        var lead = await LeadAsync(owner: _me);
        var conversation = await ConversationAsync(lead.Id, null, holder: _me);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var word = await FileAsync(project.Id, "proposition.docx", Docx, [0x50, 0x4B, 0x03, 0x04]);

        var panel = (await PanelAsync(project.Id)).Value!;

        panel.Recipient.Should().Be("Lead");
        panel.RecipientId.Should().Be(lead.Id);
        panel.ConversationId.Should().Be(conversation.Id);
        panel.HeldByMe.Should().BeTrue();
        panel.WindowOpen.Should().BeTrue();
        panel.Blockers.Should().BeEmpty();
        panel.Files.Single(f => f.AttachmentId == devis.Id).Sendable.Should().BeTrue();
        panel.Files.Single(f => f.AttachmentId == word.Id).Reason.Should().Be(WhatsAppConversationErrors.MediaTypeNotAllowed, "a Word file goes by e-mail only");
    }

    [Fact]
    public async Task The_files_leave_one_message_each_the_caption_on_the_first_all_linked_to_the_project()
    {
        var lead = await LeadAsync(owner: _me);
        var conversation = await ConversationAsync(lead.Id, null, holder: _me);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var photo = await FileAsync(project.Id, "tracteur.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D]);

        var sent = await SendAsync(project.Id, "Voici votre devis.", devis.Id, photo.Id);

        sent.IsSuccess.Should().BeTrue(sent.Error);
        sent.Value!.Select(s => s.Status).Should().Equal("Sent", "Sent");

        await using var read = _db.CreateContext();
        var messages = await read.WhatsAppMessages.OrderBy(m => m.MediaFileName).ToListAsync();
        messages.Should().HaveCount(2).And.OnlyContain(m => m.OpportunityId == project.Id && m.ConversationId == conversation.Id);
        messages.Single(m => m.MediaFileName == "devis.pdf").Body.Should().Be("Voici votre devis.");
        messages.Single(m => m.MediaFileName == "tracteur.png").Body.Should().BeNull();
        messages.Single(m => m.MediaFileName == "devis.pdf").ProjectAttachmentId.Should().Be(devis.Id);

        // The project's trail (lot 4): one line for what left, each file handed over.
        var line = await read.ProjectHistory.SingleAsync();
        line.Kind.Should().Be(ProjectHistoryKind.DocumentSent);
        line.To.Should().Be(ProjectDocumentChannels.WhatsApp);
        line.Detail.Should().Be("devis.pdf ; tracteur.png");
        line.ActorId.Should().Be(_me);
        (await read.ProjectAttachments.Where(a => a.LastSentChannel == ProjectDocumentChannels.WhatsApp).CountAsync()).Should().Be(2);
        await _messaging.Received(2).SendMediaAsync(_tenant, _connection, Phone, Arg.Any<MessagingMediaContent>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Word_file_in_the_selection_sends_nothing_rather_than_half()
    {
        var lead = await LeadAsync(owner: _me);
        await ConversationAsync(lead.Id, null, holder: _me);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var word = await FileAsync(project.Id, "proposition.docx", Docx, [0x50, 0x4B, 0x03, 0x04]);

        (await SendAsync(project.Id, null, devis.Id, word.Id)).Error.Should().Be(WhatsAppConversationErrors.MediaTypeNotAllowed);

        await _messaging.DidNotReceiveWithAnyArgs().SendMediaAsync(default, default, default!, default!, default, default);
        await using var read = _db.CreateContext();
        (await read.WhatsAppMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_lead_s_rules_apply_holder_window_consent()
    {
        var lead = await LeadAsync(owner: _me);
        var conversation = await ConversationAsync(lead.Id, null, holder: _colleague);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");

        (await PanelAsync(project.Id)).Value!.Blockers.Should().Equal(WhatsAppConversationErrors.NotHolder);
        (await SendAsync(project.Id, null, devis.Id)).Error.Should().Be(WhatsAppConversationErrors.NotHolder);

        await using (var db = _db.CreateContext())
        {
            var c = await db.WhatsAppConversations.AsTracking().SingleAsync(x => x.Id == conversation.Id);
            c.Reassign(_me, TimeSpan.FromMinutes(30), _now).IsSuccess.Should().BeTrue();
            typeof(WhatsAppConversation).GetProperty(nameof(WhatsAppConversation.WindowExpiresAt))!.SetValue(c, _now.AddMinutes(-1));
            await db.SaveChangesAsync();
        }
        (await SendAsync(project.Id, null, devis.Id)).Error.Should().Be(WhatsAppConversationErrors.WindowClosed);

        await using (var db = _db.CreateContext())
        {
            var c = await db.WhatsAppConversations.AsTracking().SingleAsync(x => x.Id == conversation.Id);
            c.RecordInbound(_now.AddMinutes(-5), "Awa");
            var consent = await db.LeadConsents.AsTracking().SingleAsync();
            consent.Withdraw(_me, TimeProvider.System, "STOP").IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }
        (await SendAsync(project.Id, null, devis.Id)).Error.Should().Be(WhatsAppErrors.ConsentRevoked);
        await _messaging.DidNotReceiveWithAnyArgs().SendMediaAsync(default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task The_number_s_conversation_on_another_record_is_not_written_in()
    {
        var lead = await LeadAsync(owner: _me);
        await ConversationAsync(Guid.NewGuid(), null, holder: _me); // the same number, filed on another lead
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");

        (await PanelAsync(project.Id)).Value!.Blockers.Should().Equal(WhatsAppConversationErrors.ConversationNotFound);
        (await SendAsync(project.Id, null, devis.Id)).Error.Should().Be(WhatsAppConversationErrors.ConversationNotFound);
    }

    [Fact]
    public async Task A_project_outside_the_agent_s_perimeter_does_not_exist_and_a_file_of_another_project_is_refused()
    {
        var theirs = await LeadAsync(owner: _colleague);
        var theirProject = await LeadProjectAsync(theirs.Id);
        var mine = await LeadAsync(owner: _me);
        await ConversationAsync(mine.Id, null, holder: _me);
        var myProject = await LeadProjectAsync(mine.Id);
        var elsewhere = await FileAsync(theirProject.Id, "devis.pdf", "application/pdf");

        (await PanelAsync(theirProject.Id)).Error.Should().Be(OpportunityErrors.NotFound);
        (await SendAsync(theirProject.Id, null, elsewhere.Id)).Error.Should().Be(OpportunityErrors.NotFound);
        (await SendAsync(myProject.Id, null, elsewhere.Id)).Error.Should().Be(ProjectAttachmentHandlers.NotFound);
        (await SendAsync(myProject.Id, null)).Error.Should().Be(ProjectWhatsAppErrors.FilesInvalid);
    }

    // ── To the project's client ────────────────────────────────────────────────

    private Guid ClientInPerimeter()
    {
        var client = Guid.NewGuid();
        _customers.GetClientSummaryAsync(_tenant, client, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(client, "CLI-001", "Individual", "Awa Koné", "Active", Guid.NewGuid(), _me, "Approved", "Low", null));
        _customers.CanActorAccessClientAsync(_tenant, client, _me, Arg.Any<CancellationToken>()).Returns(true);
        _customers.ResolvePhoneForCallAsync(_tenant, client, PhoneSlot.Primary, _me, Arg.Any<CancellationToken>()).Returns(Result.Ok(Phone));
        return client;
    }

    [Fact]
    public async Task A_project_opened_on_a_client_writes_to_the_client_without_consent()
    {
        var client = ClientInPerimeter();
        var conversation = await ConversationAsync(null, client, holder: _me);
        Opportunity project;
        await using (var db = _db.CreateContext())
        {
            project = Opportunity.CreateForCustomer(_tenant, client, "Assurance obsèques", "OBS", _me, TimeProvider.System);
            db.Opportunities.Add(project);
            await db.SaveChangesAsync();
        }
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");

        (await PanelAsync(project.Id)).Value!.Recipient.Should().Be("Client");
        var sent = await SendAsync(project.Id, null, devis.Id);

        sent.IsSuccess.Should().BeTrue(sent.Error);
        await using var read = _db.CreateContext();
        var message = await read.WhatsAppMessages.SingleAsync();
        message.ConversationId.Should().Be(conversation.Id);
        message.OpportunityId.Should().Be(project.Id);
    }

    [Fact]
    public async Task A_client_receives_an_image_or_a_PDF_about_a_complaint_and_nothing_else()
    {
        var client = ClientInPerimeter();
        var complaint = Guid.NewGuid();
        _customers.GetComplaintReferenceAsync(_tenant, client, complaint, Arg.Any<CancellationToken>())
            .Returns(new ClientComplaintReference(complaint, "RCL-2026-000042", true));
        await ConversationAsync(null, client, holder: _me);

        async Task<Result<SendWhatsAppMediaResult>> MediaAsync(byte[] content, string type)
        {
            await using var db = _db.CreateContext();
            return await new SendClientWhatsAppMediaHandler(ClientContext(db), _user, Sender(db), new FixedClock(_now))
                .Handle(new SendClientWhatsAppMediaCommand(client, content, type, "reponse.pdf", "Notre réponse", complaint), default);
        }

        (await MediaAsync([0x50, 0x4B, 0x03, 0x04], Docx)).Error.Should().Be(WhatsAppConversationErrors.MediaTypeNotAllowed);
        (await MediaAsync(Encoding.UTF8.GetBytes("pas un pdf"), "application/pdf")).Error.Should().Be(WhatsAppConversationErrors.MediaInvalid);

        var sent = await MediaAsync(Encoding.UTF8.GetBytes("%PDF-1.7 réponse"), "application/pdf");
        sent.IsSuccess.Should().BeTrue(sent.Error);
        sent.Value!.Status.Should().Be("Sent");

        await using var read = _db.CreateContext();
        var message = await read.WhatsAppMessages.SingleAsync();
        message.ComplaintId.Should().Be(complaint);
        message.Kind.Should().Be(WhatsAppMessageKind.Document);
    }

    [Fact]
    public async Task A_provider_refusal_is_a_failed_message_answered_per_file()
    {
        var lead = await LeadAsync(owner: _me);
        await ConversationAsync(lead.Id, null, holder: _me);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        _messaging.SendMediaAsync(default, default, default!, default!, default, default)
            .ReturnsForAnyArgs(IntegrationResult.Functional<MessagingMediaSent>("MESSAGING_TEST_RECIPIENT"));

        var sent = await SendAsync(project.Id, null, devis.Id);

        sent.IsSuccess.Should().BeTrue();
        sent.Value!.Single().Should().BeEquivalentTo(new { AttachmentId = devis.Id, Status = "Failed", ErrorCode = "MESSAGING_TEST_RECIPIENT" });
        await using var read = _db.CreateContext();
        (await read.ProjectHistory.CountAsync()).Should().Be(0, "nothing left: the history says what happened");
        (await read.ProjectAttachments.SingleAsync()).LastSentAt.Should().BeNull();
    }

    // ── Outside the window: a template whose header carries the file (lot 3) ──

    private async Task<WhatsAppTemplate> TemplateAsync(string name, string? headerFormat, string? headerText = null)
    {
        await using var db = _db.CreateContext();
        var template = WhatsAppTemplate.Discover(_tenant, _connection, name, "fr", _now);
        template.Refresh("UTILITY", "APPROVED", null, headerText, "Bonjour {{1}}, voici le document de votre projet.", null, [], _now, headerFormat);
        db.WhatsAppTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    /// <summary>The mediator of the project's route, sending the record's own template commands to their real handlers.</summary>
    private MediatR.ISender Mediator(LeadsDbContext db, IServiceProvider services)
    {
        var sender = Substitute.For<MediatR.ISender>();
        sender.Send(Arg.Any<SendWhatsAppTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => new SendWhatsAppTemplateHandler(db, _user, new LeadAccessGuard(_user, _admin), _admin, _integration, _indexer,
                    _publisher, new FixedClock(_now), _customers, services)
                .Handle(call.Arg<SendWhatsAppTemplateCommand>(), default));
        sender.Send(Arg.Any<SendClientWhatsAppTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => new SendClientWhatsAppTemplateHandler(db, ClientContext(db), _user, _publisher, new FixedClock(_now), services)
                .Handle(call.Arg<SendClientWhatsAppTemplateCommand>(), default));
        return sender;
    }

    private async Task<Result<SendWhatsAppTemplateResult>> SendTemplateAsync(Guid projectId, Guid templateId, Guid attachmentId, bool withStore = true)
    {
        await using var db = _db.CreateContext();
        var services = withStore
            ? new Services((typeof(Sankore.Modules.Leads.Infrastructure.WhatsAppMedia.IWhatsAppMediaStore), _media))
            : new Services();
        return await new SendProjectWhatsAppTemplateHandler(Target(db), Reader(db), _user, Mediator(db, services))
            .Handle(new SendProjectWhatsAppTemplateCommand(projectId, templateId, attachmentId, ["Awa"], MayForce: false), default);
    }

    [Fact]
    public void A_template_s_header_says_whether_it_is_sent_with_a_file()
    {
        var template = WhatsAppTemplate.Discover(_tenant, _connection, "envoi_devis", "fr", _now);

        template.Refresh("UTILITY", "APPROVED", null, null, "Bonjour {{1}}", null, [], _now, "document");
        template.FileHeader.Should().Be("DOCUMENT");
        template.IsSendableWithFile.Should().BeTrue();
        template.IsSendableAsText.Should().BeFalse("Meta refuses a media header sent without its file");
        template.AcceptsFile("application/pdf").Should().BeTrue();
        template.AcceptsFile("image/png").Should().BeFalse();

        template.Refresh("UTILITY", "APPROVED", null, null, "Bonjour {{1}}", null, [], _now, "VIDEO");
        template.NeedsMedia.Should().BeTrue();
        template.IsSendableWithFile.Should().BeFalse("a video is not something a project holds");

        template.Refresh("UTILITY", "APPROVED", null, "Votre dossier", "Bonjour {{1}}", null, [], _now);
        template.HeaderFormat.Should().Be("TEXT");
        template.IsSendableAsText.Should().BeTrue();
    }

    [Fact]
    public async Task A_project_s_devis_leaves_in_a_document_template_kept_encrypted_until_the_consumer_sends_it()
    {
        var lead = await LeadAsync(owner: _me);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var template = await TemplateAsync("envoi_devis", "DOCUMENT");

        var sent = await SendTemplateAsync(project.Id, template.Id, devis.Id);

        sent.IsSuccess.Should().BeTrue(sent.Error);
        await _publisher.Received(1).PublishAsync(Arg.Any<WhatsAppMessageQueuedEvent>(), Arg.Any<CancellationToken>());

        Guid messageId;
        await using (var read = _db.CreateContext())
        {
            var message = await read.WhatsAppMessages.SingleAsync();
            messageId = message.Id;
            message.Kind.Should().Be(WhatsAppMessageKind.Template);
            message.OpportunityId.Should().Be(project.Id);
            message.MediaFileName.Should().Be("devis.pdf");
            message.MediaMimeType.Should().Be("application/pdf");
            _media.Files.Should().ContainKey(message.MediaStorageRef!);
        }

        // The consumer reads the file back and gives it to the template's header.
        _messaging.SendTemplateAsync(_tenant, _connection, Arg.Any<MessagingTemplateSend>(), Arg.Any<CancellationToken>())
            .Returns(IntegrationResult.Ok("wamid.tpl"));
        await using (var db = _db.CreateContext())
        {
            await SendWhatsAppMessageConsumer.SendAsync(db, _integration, Substitute.For<IRealtimeNotifier>(), new FixedClock(_now),
                NullLogger.Instance, _tenant, messageId, default, _media, new ProjectDocumentTrail(db, new FixedClock(_now)));
        }

        await using (var trail = _db.CreateContext())
        {
            var line = await trail.ProjectHistory.SingleAsync();
            line.Kind.Should().Be(ProjectHistoryKind.DocumentSent);
            line.RelatedId.Should().Be(messageId);
            line.ActorId.Should().Be(_me, "the agent who sent it, though the consumer acts for nobody");
            (await trail.ProjectAttachments.SingleAsync()).LastSentChannel.Should().Be(ProjectDocumentChannels.WhatsApp);
        }

        await _messaging.Received(1).SendTemplateAsync(_tenant, _connection,
            Arg.Is<MessagingTemplateSend>(m => m.Name == "envoi_devis" && m.HeaderMedia != null
                && m.HeaderMedia.FileName == "devis.pdf" && m.HeaderMedia.ContentType == "application/pdf"
                && Encoding.UTF8.GetString(m.HeaderMedia.Content).StartsWith("%PDF")),
            Arg.Any<CancellationToken>());
        await using var verify = _db.CreateContext();
        (await verify.WhatsAppMessages.SingleAsync()).Status.Should().Be(WhatsAppMessageStatus.Sent);
    }

    [Fact]
    public async Task The_file_must_be_what_the_header_takes_and_the_template_must_take_one()
    {
        var lead = await LeadAsync(owner: _me);
        var project = await LeadProjectAsync(lead.Id);
        var word = await FileAsync(project.Id, "proposition.docx", Docx, [0x50, 0x4B, 0x03, 0x04]);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var document = await TemplateAsync("envoi_devis", "DOCUMENT");
        var image = await TemplateAsync("envoi_photo", "IMAGE");
        var text = await TemplateAsync("relance", "TEXT", "Votre dossier");

        (await SendTemplateAsync(project.Id, document.Id, word.Id)).Error.Should().Be(WhatsAppConversationErrors.MediaTypeNotAllowed);
        (await SendTemplateAsync(project.Id, image.Id, devis.Id)).Error.Should().Be(WhatsAppConversationErrors.MediaTypeNotAllowed);
        (await SendTemplateAsync(project.Id, text.Id, devis.Id)).Error.Should().Be(WhatsAppTemplateFiles.NoFileHeader);
        (await SendTemplateAsync(project.Id, document.Id, devis.Id, withStore: false)).Error.Should().Be(WhatsAppTemplateFiles.StoreUnavailable);

        await using var read = _db.CreateContext();
        (await read.WhatsAppMessages.CountAsync()).Should().Be(0);
        _media.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task The_lead_s_template_rules_still_apply_and_its_drawer_never_offers_a_file_template()
    {
        var lead = await LeadAsync(owner: _me, consent: false);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var document = await TemplateAsync("envoi_devis", "DOCUMENT");

        (await SendTemplateAsync(project.Id, document.Id, devis.Id)).Error.Should().Be(WhatsAppErrors.ConsentMissing);

        // Without a file, the lead's own route refuses a template whose header wants one.
        var consented = await LeadAsync(owner: _me);
        await using (var db = _db.CreateContext())
        {
            (await new SendWhatsAppTemplateHandler(db, _user, new LeadAccessGuard(_user, _admin), _admin, _integration, _indexer,
                    _publisher, new FixedClock(_now), _customers)
                .Handle(new SendWhatsAppTemplateCommand(consented.Id, document.Id, ["Awa"], MayForce: false), default))
                .Error.Should().Be(WhatsAppTemplateFiles.NeedsFile);

            var drawer = await new ComposeWhatsAppHandler(db, new WhatsAppLeadAccess(db, new LeadAccessGuard(_user, _admin)), _user, _admin, _integration)
                .Handle(new ComposeWhatsAppQuery(consented.Id, HasWhatsAppPermission: true), default);
            drawer.Value!.Should().BeEmpty();

            var withFile = await new ComposeWhatsAppHandler(db, new WhatsAppLeadAccess(db, new LeadAccessGuard(_user, _admin)), _user, _admin, _integration)
                .Handle(new ComposeWhatsAppQuery(consented.Id, HasWhatsAppPermission: true, WithFile: true), default);
            withFile.Value!.Should().ContainSingle().Which.HeaderFormat.Should().Be("DOCUMENT");
        }
    }

    [Fact]
    public async Task A_client_project_s_devis_goes_in_the_client_s_template_without_consent()
    {
        var client = ClientInPerimeter();
        Opportunity project;
        await using (var db = _db.CreateContext())
        {
            project = Opportunity.CreateForCustomer(_tenant, client, "Assurance obsèques", "OBS", _me, TimeProvider.System);
            db.Opportunities.Add(project);
            await db.SaveChangesAsync();
        }
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var template = await TemplateAsync("envoi_devis", "DOCUMENT");

        var sent = await SendTemplateAsync(project.Id, template.Id, devis.Id);

        sent.IsSuccess.Should().BeTrue(sent.Error);
        await using var read = _db.CreateContext();
        var message = await read.WhatsAppMessages.SingleAsync();
        message.OpportunityId.Should().Be(project.Id);
        (await read.WhatsAppConversations.SingleAsync()).ClientId.Should().Be(client);
    }

    [Fact]
    public async Task A_file_gone_from_the_store_fails_the_message_rather_than_sending_the_template_bare()
    {
        var lead = await LeadAsync(owner: _me);
        var project = await LeadProjectAsync(lead.Id);
        var devis = await FileAsync(project.Id, "devis.pdf", "application/pdf");
        var template = await TemplateAsync("envoi_devis", "DOCUMENT");
        (await SendTemplateAsync(project.Id, template.Id, devis.Id)).IsSuccess.Should().BeTrue();
        _media.Files.Clear();

        await using (var db = _db.CreateContext())
        {
            var id = (await db.WhatsAppMessages.SingleAsync()).Id;
            await SendWhatsAppMessageConsumer.SendAsync(db, _integration, Substitute.For<IRealtimeNotifier>(), new FixedClock(_now),
                NullLogger.Instance, _tenant, id, default, _media);
        }

        await _messaging.DidNotReceiveWithAnyArgs().SendTemplateAsync(default, default, default!, default);
        await using var read = _db.CreateContext();
        (await read.WhatsAppMessages.SingleAsync()).ErrorCode.Should().Be(SendWhatsAppMessageConsumer.TemplateFileMissing);
    }
}
