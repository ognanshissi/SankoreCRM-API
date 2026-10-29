namespace Sankore.Modules.Leads.Tests.Features.WorkflowNotifications;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.WorkflowNotifications;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A workflow « Notification » step writing to the person of a lead or a project. A lead is a
/// prospect: nothing leaves without the channel's consent, given by the person themself.
/// </summary>
public sealed class LeadWorkflowNotifierTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public LeadWorkflowNotifierTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _notifications.QueueEmailAsync(default!, default).ReturnsForAnyArgs(Result.Ok(Guid.NewGuid()));
        _notifications.QueueSmsAsync(default!, default).ReturnsForAnyArgs(Result.Ok(Guid.NewGuid()));
        _administration.GetUserNamesAsync(default, default!, default)
            .ReturnsForAnyArgs(new Dictionary<Guid, string>());
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task An_email_goes_to_a_lead_who_consented_with_the_variables_replaced()
    {
        var lead = await SeedLeadAsync(email: "awa@example.test", consents: [ConsentType.EmailContact]);

        var outcome = await Notifier().SendAsync(Request(lead.Id, "Lead", "Email", "Bonjour {{ nom }}, dossier reçu."), default);

        outcome.Sent.Should().BeTrue();
        outcome.Detail.Should().Be("E-mail mis en file pour le lead.");
        await _notifications.Received(1).QueueEmailAsync(Arg.Is<QueueEmailRequest>(r =>
            r.TemplateKey == WorkflowNotificationRequested.TemplateKey
            && r.RecipientEmail == "awa@example.test"
            && (string)r.TemplateData["body"] == "Bonjour Awa Ndiaye, dossier reçu."
            && (string)r.TemplateData["subject"] == "Prévenir le client"
            && r.IdempotencyKey.EndsWith(":subject:email")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_the_email_consent_nothing_leaves()
    {
        var lead = await SeedLeadAsync(email: "awa@example.test", consents: [ConsentType.SmsContact, ConsentType.Marketing]);

        var outcome = await Notifier().SendAsync(Request(lead.Id, "Lead", "Email", "Bonjour"), default);

        outcome.Sent.Should().BeFalse();
        outcome.Detail.Should().Be("Non envoyé : le lead n'a pas consenti au contact par e-mail.");
        await _notifications.DidNotReceiveWithAnyArgs().QueueEmailAsync(default!, default);
    }

    [Fact]
    public async Task A_household_members_consent_does_not_count_for_the_lead()
    {
        var lead = await SeedLeadAsync(email: "awa@example.test", consents: [], memberConsent: ConsentType.EmailContact);

        var outcome = await Notifier().SendAsync(Request(lead.Id, "Lead", "Email", "Bonjour"), default);

        outcome.Sent.Should().BeFalse();
        outcome.Detail.Should().Be("Non envoyé : le lead n'a pas consenti au contact par e-mail.");
    }

    [Fact]
    public async Task A_text_message_needs_the_SMS_consent_and_a_working_channel()
    {
        var lead = await SeedLeadAsync(email: null, consents: [ConsentType.SmsContact]);

        _administration.GetSmsConfigAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto("Twilio", "SANKORE", "AC1", "221", true, false, null, 0));
        (await Notifier().SendAsync(Request(lead.Id, "Lead", "Sms", "Votre dossier avance."), default))
            .Detail.Should().Be("Non envoyé : aucun canal SMS n'est configuré.");

        _administration.GetSmsConfigAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto("Twilio", "SANKORE", "AC1", "221", true, true, null, 0));
        (await Notifier().SendAsync(Request(lead.Id, "Lead", "Sms", "Votre dossier avance."), default))
            .Detail.Should().Be("SMS mis en file pour le lead.");

        await _notifications.Received(1).QueueSmsAsync(Arg.Is<QueueSmsRequest>(r =>
            r.RecipientPhone == "+221771234567"
            && (string)r.TemplateData["body"] == "Votre dossier avance."
            && r.IdempotencyKey.EndsWith(":subject:sms")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_project_for_a_client_goes_through_the_client_module_unless_restricted()
    {
        var clientId = Guid.NewGuid();
        var project = Opportunity.CreateForCustomer(_tenantId, clientId, "Crédit", "PRET-IMMO", null, TimeProvider.System);
        await using (var db = _factory.CreateContext())
        {
            db.Opportunities.Add(project);
            await db.SaveChangesAsync();
        }
        _customers.GetClientSummaryAsync(_tenantId, clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientSummary(clientId, "C-1", "Individual", "DIOP Moussa", "Active", Guid.NewGuid(), null, "Approved", "Low", null));
        _customers.NotifyClientAsync(default!, default).ReturnsForAnyArgs(true);

        var outcome = await Notifier().SendAsync(Request(project.Id, "Opportunity", "Email", "{{nom}} : {{produit}} validé"), default);

        outcome.Detail.Should().Be("E-mail mis en file pour le client.");
        await _customers.Received(1).NotifyClientAsync(Arg.Is<NotifyClientRequest>(r =>
            r.ClientId == clientId
            && r.TemplateKey == WorkflowNotificationRequested.TemplateKey
            && (string)r.TemplateData!["body"] == "DIOP Moussa : PRET-IMMO validé"), Arg.Any<CancellationToken>());

        _customers.IsProcessingRestrictedAsync(_tenantId, clientId, Arg.Any<CancellationToken>()).Returns(true);
        (await Notifier().SendAsync(Request(project.Id, "Opportunity", "Email", "Bonjour"), default))
            .Detail.Should().Be("Non envoyé : le traitement des données du client est restreint.");
        await _customers.ReceivedWithAnyArgs(1).NotifyClientAsync(default!, default);
    }

    [Fact]
    public async Task An_empty_message_sends_nothing()
    {
        var lead = await SeedLeadAsync(email: "awa@example.test", consents: [ConsentType.EmailContact]);

        (await Notifier().SendAsync(Request(lead.Id, "Lead", "Email", "  "), default)).Detail.Should().Be("Non envoyé : le message est vide.");
        await _notifications.DidNotReceiveWithAnyArgs().QueueEmailAsync(default!, default);
    }

    private LeadWorkflowNotifier Notifier()
        => new(_factory.CreateContext(), _notifications, _administration, _customers, Substitute.For<ITenantStore>());

    private WorkflowNotificationRequested Request(Guid entityId, string entityType, string channel, string message)
        => new(_tenantId, Guid.NewGuid(), Guid.NewGuid(), "Prévenir le client", entityType, entityId, channel,
            [WorkflowNotificationRecipients.Subject], [], null, message);

    private async Task<Lead> SeedLeadAsync(string? email, ConsentType[] consents, ConsentType? memberConsent = null)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Fresh().Build();
        if (email is not null) lead.SetEmail(email, "index");

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        foreach (var type in consents)
            db.LeadConsents.Add(LeadConsent.Create(_tenantId, lead.Id, type, ConsentChannel.Phone, Guid.NewGuid(), TimeProvider.System));
        if (memberConsent is { } member)
            db.LeadConsents.Add(LeadConsent.Create(_tenantId, lead.Id, member, ConsentChannel.Phone, Guid.NewGuid(), TimeProvider.System,
                subjectType: QualificationSubjectType.HouseholdMember, subjectId: Guid.NewGuid(), givenBy: Guid.NewGuid()));
        await db.SaveChangesAsync();
        return lead;
    }
}
