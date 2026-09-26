namespace Sankore.Modules.Leads.Tests.Features.QualificationTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.PublishQualificationTemplate;
using Sankore.Modules.Leads.Features.QualificationTemplates.UnarchiveQualificationTemplate;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class UnarchiveAndRepublishTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));

    public UnarchiveAndRepublishTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── Unarchive ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Unarchive_restores_an_archived_template_to_draft()
    {
        var id = await Seed(TemplateStatus.Archived);

        await using var db = _factory.CreateContext();
        var result = await new UnarchiveQualificationTemplateHandler(db).Handle(
            new UnarchiveQualificationTemplateCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var restored = await Load(id);
        restored.Status.Should().Be(TemplateStatus.Draft);

        // The history of what it has already been through is preserved.
        restored.Version.Should().Be(1);
        restored.PublishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Unarchive_does_not_put_the_template_live_again_on_its_own()
    {
        var liveId = await Seed(TemplateStatus.Published);
        var archivedId = await Seed(TemplateStatus.Archived);

        await using var db = _factory.CreateContext();
        await new UnarchiveQualificationTemplateHandler(db).Handle(
            new UnarchiveQualificationTemplateCommand(archivedId), CancellationToken.None);

        (await Load(archivedId)).Status.Should().Be(TemplateStatus.Draft);
        (await Load(liveId)).Status.Should().Be(TemplateStatus.Published);
    }

    [Theory]
    [InlineData(TemplateStatus.Draft)]
    [InlineData(TemplateStatus.Published)]
    public async Task Unarchive_is_rejected_when_the_template_is_not_archived(TemplateStatus status)
    {
        var id = await Seed(status);

        await using var db = _factory.CreateContext();
        var result = await new UnarchiveQualificationTemplateHandler(db).Handle(
            new UnarchiveQualificationTemplateCommand(id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("TEMPLATE_NOT_ARCHIVED");
        (await Load(id)).Status.Should().Be(status);
    }

    [Fact]
    public async Task Unarchive_returns_not_found_for_an_unknown_template()
    {
        await using var db = _factory.CreateContext();

        var result = await new UnarchiveQualificationTemplateHandler(db).Handle(
            new UnarchiveQualificationTemplateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("TEMPLATE_NOT_FOUND");
    }

    // ── Republish ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_archived_template_can_be_republished_directly()
    {
        var id = await Seed(TemplateStatus.Archived);

        await using var db = _factory.CreateContext();
        var result = await new PublishQualificationTemplateHandler(db, _clock).Handle(
            new PublishQualificationTemplateCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var republished = await Load(id);
        republished.Status.Should().Be(TemplateStatus.Published);
        republished.Version.Should().Be(2);                       // was 1 before archiving
        republished.PublishedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Republishing_archives_the_version_currently_live_for_the_category()
    {
        var liveId = await Seed(TemplateStatus.Published);
        var rolledBackToId = await Seed(TemplateStatus.Archived);

        await using var db = _factory.CreateContext();
        var result = await new PublishQualificationTemplateHandler(db, _clock).Handle(
            new PublishQualificationTemplateCommand(rolledBackToId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Exactly one Published template per category survives the swap.
        (await Load(rolledBackToId)).Status.Should().Be(TemplateStatus.Published);
        (await Load(liveId)).Status.Should().Be(TemplateStatus.Archived);
    }

    [Fact]
    public async Task Unarchive_then_publish_reaches_the_same_place()
    {
        var liveId = await Seed(TemplateStatus.Published);
        var id = await Seed(TemplateStatus.Archived);

        await using (var db = _factory.CreateContext())
        {
            await new UnarchiveQualificationTemplateHandler(db).Handle(
                new UnarchiveQualificationTemplateCommand(id), CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            var result = await new PublishQualificationTemplateHandler(db, _clock).Handle(
                new PublishQualificationTemplateCommand(id), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        (await Load(id)).Status.Should().Be(TemplateStatus.Published);
        (await Load(liveId)).Status.Should().Be(TemplateStatus.Archived);
    }

    [Fact]
    public async Task A_questionless_template_is_not_republished_and_the_live_one_is_left_alone()
    {
        var liveId = await Seed(TemplateStatus.Published);
        var emptyId = await Seed(TemplateStatus.Archived, withQuestion: false);

        await using var db = _factory.CreateContext();
        var result = await new PublishQualificationTemplateHandler(db, _clock).Handle(
            new PublishQualificationTemplateCommand(emptyId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("TEMPLATE_HAS_NO_QUESTIONS");

        // The incumbent must survive a failed republish — the handler checks before it
        // archives, so this holds without relying on the transaction rolling back.
        (await Load(liveId)).Status.Should().Be(TemplateStatus.Published);
        (await Load(emptyId)).Status.Should().Be(TemplateStatus.Archived);
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private async Task<Guid> Seed(TemplateStatus status, bool withQuestion = true)
    {
        await using var db = _factory.CreateContext();

        var template = QualificationTemplate.Create(
            _tenantId, $"Template {Guid.NewGuid():N}", _clock.GetUtcNow(),
            productCategory: ProductCategory.Loan);

        if (withQuestion)
            template.AddQuestion("Do you own land?", QuestionType.YesNo, weight: 10, isRequired: true);

        if (status is TemplateStatus.Published or TemplateStatus.Archived)
            template.Publish(_clock.GetUtcNow());

        if (status is TemplateStatus.Archived)
            template.Archive();

        db.QualificationTemplates.Add(template);
        await db.SaveChangesAsync();

        return template.Id;
    }

    private async Task<QualificationTemplate> Load(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.QualificationTemplates
            .Include(t => t.Questions)
            .SingleAsync(t => t.Id == id);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
