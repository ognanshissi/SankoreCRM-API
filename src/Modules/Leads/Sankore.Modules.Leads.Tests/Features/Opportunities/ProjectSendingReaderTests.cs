namespace Sankore.Modules.Leads.Tests.Features.Opportunities;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Opportunities.ProjectFields;
using Sankore.Modules.Leads.Features.Opportunities.Sending;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// What the mailbox may read of a project to send its documents (project documents lot 1,
/// 2026-11-08): the project page's perimeter, live attachments of THAT project only — and the
/// project's own attachment routes now under the same perimeter.
/// </summary>
public sealed class ProjectSendingReaderTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly TestDbContextFactory _db;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly MemoryStore _store = new();

    public ProjectSendingReaderTests() => _db = new TestDbContextFactory(_tenant);

    public void Dispose() => _db.Dispose();

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

    private sealed class Services(params (Type Type, object Service)[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.FirstOrDefault(s => s.Type == serviceType).Service;
    }

    /// <summary>An agent: without <c>lead:read:all</c>, sees the projects they own.</summary>
    private ICurrentUser Agent(Guid id)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(id);
        user.TenantId.Returns(_tenant);
        user.IsAuthenticated.Returns(true);
        user.Roles.Returns(["Agent"]);
        user.Permissions.Returns([]);
        return user;
    }

    private ProjectSendingReader Reader(LeadsDbContext db, ICurrentUser user) =>
        new(db, user, new LeadVisibility(user), new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenant)),
            new Services((typeof(ITaskAttachmentStore), _store)));

    private async Task<(Opportunity Project, Guid ClientId)> ProjectOwnedByAsync(Guid owner)
    {
        var clientId = Guid.NewGuid();
        await using var db = _db.CreateContext();
        var project = Opportunity.CreateForCustomer(_tenant, clientId, "Crédit tracteur", "Crédit de campagne", owner, TimeProvider.System);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return (project, clientId);
    }

    private async Task<ProjectAttachment> AttachAsync(Guid projectId, string name, bool removed = false, string fieldKey = "devis")
    {
        var stored = await _store.StoreAsync(_tenant, System.Text.Encoding.UTF8.GetBytes("%PDF-" + name), CancellationToken.None);
        var row = ProjectAttachment.Record(_tenant, projectId, fieldKey, name, "application/pdf", stored.SizeBytes, stored.Sha256,
            stored.StorageRef, _me, DateTimeOffset.UtcNow);
        if (removed) row.Remove(_me, DateTimeOffset.UtcNow);

        await using var db = _db.CreateContext();
        db.ProjectAttachments.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task The_owner_reads_the_project_as_a_mail_quotes_it()
    {
        var (project, clientId) = await ProjectOwnedByAsync(_me);

        await using var db = _db.CreateContext();
        var read = await Reader(db, Agent(_me)).GetAsync(_tenant, project.Id, _me, CancellationToken.None);

        read.Should().NotBeNull();
        read!.Title.Should().Be("Crédit tracteur");
        read.Product.Should().Be("Crédit de campagne");
        read.ClientId.Should().Be(clientId);
        read.StepLabel.Should().NotBeNullOrEmpty("an open project stands on a step of its template");
        read.IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task Outside_the_perimeter_or_for_somebody_else_the_project_does_not_exist()
    {
        var (theirs, _) = await ProjectOwnedByAsync(_colleague);
        var (mine, _) = await ProjectOwnedByAsync(_me);
        var file = await AttachAsync(theirs.Id, "devis.pdf");

        await using var db = _db.CreateContext();
        var reader = Reader(db, Agent(_me));

        (await reader.GetAsync(_tenant, theirs.Id, _me, CancellationToken.None)).Should().BeNull();
        (await reader.ReadFilesAsync(_tenant, theirs.Id, [file.Id], _me, CancellationToken.None))
            .Error.Should().Be(OpportunityErrors.NotFound);

        // The actor must be the signed-in caller: a job or a forged id answers nothing.
        (await reader.GetAsync(_tenant, mine.Id, _colleague, CancellationToken.None)).Should().BeNull();
        (await reader.GetAsync(Guid.NewGuid(), mine.Id, _me, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Only_the_project_s_live_attachments_are_read()
    {
        var (project, _) = await ProjectOwnedByAsync(_me);
        var (other, _) = await ProjectOwnedByAsync(_me);
        var devis = await AttachAsync(project.Id, "devis.pdf");
        var proposition = await AttachAsync(project.Id, "proposition.pdf");
        var removed = await AttachAsync(project.Id, "ancien.pdf", removed: true);
        var elsewhere = await AttachAsync(other.Id, "autre.pdf");

        await using var db = _db.CreateContext();
        var reader = Reader(db, Agent(_me));

        var read = await reader.ReadFilesAsync(_tenant, project.Id, [proposition.Id, devis.Id, devis.Id], _me, CancellationToken.None);
        read.IsSuccess.Should().BeTrue(read.Error);
        read.Value!.Select(f => f.FileName).Should().Equal("proposition.pdf", "devis.pdf");
        System.Text.Encoding.UTF8.GetString(read.Value![1].Content).Should().Be("%PDF-devis.pdf");

        (await reader.ReadFilesAsync(_tenant, project.Id, [devis.Id, removed.Id], _me, CancellationToken.None))
            .Error.Should().Be(ProjectAttachmentHandlers.NotFound);
        (await reader.ReadFilesAsync(_tenant, project.Id, [elsewhere.Id], _me, CancellationToken.None))
            .Error.Should().Be(ProjectAttachmentHandlers.NotFound);
    }

    [Fact]
    public async Task Without_a_store_nothing_can_leave()
    {
        var (project, _) = await ProjectOwnedByAsync(_me);
        var devis = await AttachAsync(project.Id, "devis.pdf");

        await using var db = _db.CreateContext();
        var user = Agent(_me);
        var reader = new ProjectSendingReader(db, user, new LeadVisibility(user),
            new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenant)), new Services());

        (await reader.ReadFilesAsync(_tenant, project.Id, [devis.Id], _me, CancellationToken.None))
            .Error.Should().Be(ProjectAttachmentHandlers.Unavailable);
    }

    [Fact]
    public async Task The_project_s_own_attachment_routes_follow_the_project_page_s_perimeter()
    {
        var (theirs, _) = await ProjectOwnedByAsync(_colleague);
        var file = await AttachAsync(theirs.Id, "devis.pdf");

        await using var db = _db.CreateContext();
        var user = Agent(_me);
        var handlers = new ProjectAttachmentHandlers(
            db, new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenant)), user,
            new Services((typeof(ITaskAttachmentStore), _store), (typeof(LeadVisibility), new LeadVisibility(user))),
            Options.Create(new TaskAttachmentStorageOptions()), TimeProvider.System, NullLogger<ProjectAttachmentHandlers>.Instance);

        (await handlers.Handle(new GetProjectAttachmentContentQuery(theirs.Id, file.Id), CancellationToken.None))
            .Error.Should().Be(ProjectAttachmentHandlers.NotFound);
        (await handlers.Handle(new RemoveProjectAttachmentCommand(theirs.Id, file.Id), CancellationToken.None))
            .Error.Should().Be(OpportunityErrors.NotFound);
        (await handlers.Handle(new StoreProjectAttachment(theirs.Id, "devis", "b.pdf", [1]), CancellationToken.None))
            .Error.Should().Be(OpportunityErrors.NotFound);
    }

    [Fact]
    public async Task Files_e_mailed_from_the_project_are_in_its_history_and_handed_over_and_another_project_s_ids_are_ignored()
    {
        var (project, _) = await ProjectOwnedByAsync(_me);
        var (other, _) = await ProjectOwnedByAsync(_colleague);
        var devis = await AttachAsync(project.Id, "devis.pdf");
        var elsewhere = await AttachAsync(other.Id, "autre.pdf");

        await using (var db = _db.CreateContext())
        {
            await new ProjectDocumentTrail(db, TimeProvider.System)
                .RecordAsync(_tenant, project.Id, ProjectDocumentChannels.Email, [devis.Id, elsewhere.Id], _me, null, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var read = _db.CreateContext();
        var line = read.ProjectHistory.Single();
        line.OpportunityId.Should().Be(project.Id);
        line.Kind.Should().Be(ProjectHistoryKind.DocumentSent);
        line.To.Should().Be(ProjectDocumentChannels.Email);
        line.Detail.Should().Be("devis.pdf");
        read.ProjectAttachments.Single(a => a.Id == devis.Id).LastSentChannel.Should().Be(ProjectDocumentChannels.Email);
        read.ProjectAttachments.Single(a => a.Id == elsewhere.Id).LastSentAt.Should().BeNull();
    }

    [Fact]
    public async Task A_generated_quote_that_leaves_ticks_devis_envoye_and_an_uploaded_file_does_not()
    {
        // 2026-11-10, product owner: the CRM ticks what it saw happen.
        var (project, _) = await ProjectOwnedByAsync(_me);
        var upload = await AttachAsync(project.Id, "photo.pdf");
        var quote = await AttachAsync(project.Id, "Devis DEV-2026-000001.pdf", fieldKey: "quote");

        async Task SendAsync(Guid file, string channel)
        {
            await using var db = _db.CreateContext();
            await new ProjectDocumentTrail(db, TimeProvider.System)
                .RecordAsync(_tenant, project.Id, channel, [file], _me, null, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await SendAsync(upload.Id, ProjectDocumentChannels.Email);
        await using (var read = _db.CreateContext())
            read.Opportunities.Single(o => o.Id == project.Id).CheckedChecklistItems
                .Should().NotContain(ProjectTemplate.QuoteSentChecklistItemId);

        await SendAsync(quote.Id, ProjectDocumentChannels.WhatsApp);
        await using (var read = _db.CreateContext())
            read.Opportunities.Single(o => o.Id == project.Id).CheckedChecklistItems
                .Should().Contain(ProjectTemplate.QuoteSentChecklistItemId);
    }
}
