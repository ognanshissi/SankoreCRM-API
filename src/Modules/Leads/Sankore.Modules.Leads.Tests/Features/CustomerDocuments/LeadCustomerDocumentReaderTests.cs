namespace Sankore.Modules.Leads.Tests.Features.CustomerDocuments;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CustomerDocuments;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Which of this module's files belong to a client (product owner, 2026-10-26): through the client
/// itself, or through a lead attached to or converted into them — never another client's.
/// </summary>
public sealed class LeadCustomerDocumentReaderTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
    private readonly TestDbContextFactory _factory;

    public LeadCustomerDocumentReaderTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<IReadOnlyList<CustomerDocumentEntry>> ReadAsync()
    {
        await using var db = _factory.CreateContext();
        return await LeadCustomerDocumentReader.ReadAsync(db, _tenant, _client, CancellationToken.None);
    }

    private Lead Lead(Guid? attachedTo = null)
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        if (attachedTo is { } customer)
            lead.AttachToCustomer(customer, isAutomatic: true, reason: null).IsSuccess.Should().BeTrue();
        return lead;
    }

    private ProjectAttachment ProjectFile(Guid projectId, string name)
        => ProjectAttachment.Record(_tenant, projectId, "devis", name, "application/pdf", 100, "sha", "ref", _agent, _now);

    [Fact]
    public async Task Project_files_come_from_the_client_s_projects_and_its_leads_projects_only()
    {
        await using (var db = _factory.CreateContext())
        {
            var mine = Lead(attachedTo: _client);
            var someoneElse = Lead(attachedTo: Guid.NewGuid());
            db.Leads.AddRange(mine, someoneElse);

            var onClient = Opportunity.CreateForCustomer(_tenant, _client, "Projet client", "OBS", _agent, TimeProvider.System);
            var onLead = Opportunity.CreateFromLead(_tenant, mine.Id, "Projet du lead", "OBS", _agent, TimeProvider.System);
            var foreign = Opportunity.CreateFromLead(_tenant, someoneElse.Id, "Autre", "OBS", _agent, TimeProvider.System);
            db.Opportunities.AddRange(onClient, onLead, foreign);

            var removed = ProjectFile(onClient.Id, "retire.pdf");
            removed.Remove(_agent, _now);
            db.ProjectAttachments.AddRange(
                ProjectFile(onClient.Id, "devis-client.pdf"),
                ProjectFile(onLead.Id, "devis-lead.pdf"),
                ProjectFile(foreign.Id, "autre.pdf"),
                removed);
            await db.SaveChangesAsync();
        }

        var files = await ReadAsync();

        files.Select(f => f.FileName).Should().BeEquivalentTo(["devis-client.pdf", "devis-lead.pdf"]);
        files.Should().OnlyContain(f => f.Source == "Project" && f.Direction == CustomerDocumentDirection.Collected);
        files.Should().OnlyContain(f => f.ContentPath!.StartsWith("leads/opportunities/"));
    }

    [Fact]
    public async Task Task_files_come_from_tasks_about_the_client_or_its_leads()
    {
        await using (var db = _factory.CreateContext())
        {
            var mine = Lead(attachedTo: _client);
            db.Leads.Add(mine);

            var aboutClient = CrmTask.Create(_tenant, CrmTaskType.FirstContact, CrmTaskPriority.Low, "Rappeler", _now).AboutClient(_client);
            var aboutLead = CrmTask.Create(_tenant, CrmTaskType.FirstContact, CrmTaskPriority.Low, "Relancer", _now, leadId: mine.Id);
            var internalTask = CrmTask.Create(_tenant, CrmTaskType.FirstContact, CrmTaskPriority.Low, "Interne", _now);
            db.CrmTasks.AddRange(aboutClient, aboutLead, internalTask);

            db.CrmTaskAttachments.AddRange(
                CrmTaskAttachment.Record(_tenant, aboutClient.Id, "courrier.pdf", "application/pdf", 10, "s", "r", _agent, _now),
                CrmTaskAttachment.Record(_tenant, aboutLead.Id, "photo.jpg", "image/jpeg", 10, "s", "r", _agent, _now),
                CrmTaskAttachment.Record(_tenant, internalTask.Id, "interne.pdf", "application/pdf", 10, "s", "r", _agent, _now));
            await db.SaveChangesAsync();
        }

        var files = await ReadAsync();

        files.Select(f => f.FileName).Should().BeEquivalentTo(["courrier.pdf", "photo.jpg"]);
        files.Should().OnlyContain(f => f.OpenPermissions.Contains(Permissions.CanReadCrmTasks.Code));
    }

    [Fact]
    public async Task Task_files_come_from_tasks_about_the_client_s_projects_too()
    {
        await using (var db = _factory.CreateContext())
        {
            var onClient = Opportunity.CreateForCustomer(_tenant, _client, "Projet client", "OBS", _agent, TimeProvider.System);
            var foreign = Opportunity.CreateForCustomer(_tenant, Guid.NewGuid(), "Autre client", "OBS", _agent, TimeProvider.System);
            db.Opportunities.AddRange(onClient, foreign);

            var aboutProject = CrmTask.CreateManual(_tenant, _agent, CrmTaskType.Generic, CrmTaskPriority.Low, "Devis",
                _now.AddDays(1), TaskEntityType.Project, onClient.Id, _agent, null, _now).Value!;
            var aboutOther = CrmTask.CreateManual(_tenant, _agent, CrmTaskType.Generic, CrmTaskPriority.Low, "Ailleurs",
                _now.AddDays(1), TaskEntityType.Project, foreign.Id, _agent, null, _now).Value!;
            db.CrmTasks.AddRange(aboutProject, aboutOther);

            db.CrmTaskAttachments.AddRange(
                CrmTaskAttachment.Record(_tenant, aboutProject.Id, "devis.pdf", "application/pdf", 10, "s", "r", _agent, _now),
                CrmTaskAttachment.Record(_tenant, aboutOther.Id, "autre.pdf", "application/pdf", 10, "s", "r", _agent, _now));
            await db.SaveChangesAsync();
        }

        var files = await ReadAsync();

        files.Where(f => f.Source == "Task").Select(f => f.FileName).Should().Equal("devis.pdf");
    }

    [Fact]
    public async Task WhatsApp_media_are_collected_or_delivered_by_direction_and_a_purged_one_is_left_out()
    {
        await using (var db = _factory.CreateContext())
        {
            var conversation = WhatsAppConversation.Open(
                _tenant, Guid.NewGuid(), "+2250700000001", "idx", leadId: null, clientId: _client,
                contactName: null, ambiguousMatch: false, now: _now);
            db.WhatsAppConversations.Add(conversation);

            var received = WhatsAppMessage.Inbound(_tenant, conversation.Id, "wamid.1", WhatsAppMessageKind.Image,
                null, "media-1", "image/jpeg", "cni.jpg", null, _now, _now);
            var sent = WhatsAppMessage.OutboundMedia(_tenant, conversation.Id, _agent, WhatsAppMessageKind.Document,
                "application/pdf", "fiche.pdf", null, _now);
            var purged = WhatsAppMessage.Inbound(_tenant, conversation.Id, "wamid.2", WhatsAppMessageKind.Image,
                null, "media-2", "image/jpeg", "vieux.jpg", null, _now, _now);
            purged.MarkMediaPurged(_now);
            var text = WhatsAppMessage.Inbound(_tenant, conversation.Id, "wamid.3", WhatsAppMessageKind.Text,
                "Bonjour", null, null, null, null, _now, _now);
            db.WhatsAppMessages.AddRange(received, sent, purged, text);
            await db.SaveChangesAsync();
        }

        var files = await ReadAsync();

        files.Should().HaveCount(2);
        files.Single(f => f.FileName == "cni.jpg").Direction.Should().Be(CustomerDocumentDirection.Collected);
        files.Single(f => f.FileName == "fiche.pdf").Direction.Should().Be(CustomerDocumentDirection.Delivered,
            "an outbound media is listed before the provider gives it an id");
        files.Should().OnlyContain(f => f.ContentPath!.StartsWith("leads/whatsapp/conversations/"));
    }
}
