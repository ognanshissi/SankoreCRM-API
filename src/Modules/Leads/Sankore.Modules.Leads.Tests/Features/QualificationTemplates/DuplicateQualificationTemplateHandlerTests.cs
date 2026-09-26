namespace Sankore.Modules.Leads.Tests.Features.QualificationTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.DuplicateQualificationTemplate;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class DuplicateQualificationTemplateHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-26T10:00:00Z"));

    public DuplicateQualificationTemplateHandlerTests()
        => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(TemplateStatus.Draft)]
    [InlineData(TemplateStatus.Published)]
    [InlineData(TemplateStatus.Archived)]
    public async Task Duplicates_a_template_in_any_status_into_a_new_draft(TemplateStatus sourceStatus)
    {
        var sourceId = await SeedTemplate(status: sourceStatus);

        await using var db = _factory.CreateContext();
        var result = await new DuplicateQualificationTemplateHandler(db, _clock).Handle(
            new DuplicateQualificationTemplateCommand(sourceId, Name: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(sourceId);

        var copy = await LoadTemplate(result.Value);
        copy.Status.Should().Be(TemplateStatus.Draft);
        copy.Version.Should().Be(0);
        copy.PublishedAt.Should().BeNull();
        copy.CreatedAt.Should().Be(_clock.GetUtcNow());
        copy.Name.Should().Be("Agri loan intake (copy)");

        // Product scope carries over so the copy can supersede the original once published.
        copy.ProductCategory.Should().Be(ProductCategory.Loan);
        copy.ProductCode.Should().Be("CRED-AGRI-01");

        // The source is untouched.
        var source = await LoadTemplate(sourceId);
        source.Status.Should().Be(sourceStatus);
        source.Questions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Copies_sections_and_questions_with_fresh_ids()
    {
        var sourceId = await SeedTemplate(status: TemplateStatus.Published);
        var source = await LoadTemplate(sourceId);

        await using var db = _factory.CreateContext();
        var result = await new DuplicateQualificationTemplateHandler(db, _clock).Handle(
            new DuplicateQualificationTemplateCommand(sourceId, Name: null), CancellationToken.None);

        var copy = await LoadTemplate(result.Value);

        copy.Sections.Should().HaveCount(1);
        copy.Questions.Should().HaveCount(2);

        copy.Sections.Select(s => s.Id).Should().NotIntersectWith(source.Sections.Select(s => s.Id));
        copy.Questions.Select(q => q.Id).Should().NotIntersectWith(source.Questions.Select(q => q.Id));

        // Children belong to the copy, not the source.
        copy.Sections.Should().OnlyContain(s => s.TemplateId == copy.Id);
        copy.Questions.Should().OnlyContain(q => q.TemplateId == copy.Id);

        var copiedQuestions = copy.Questions.OrderBy(q => q.Order).ToList();
        copiedQuestions[0].Label.Should().Be("Do you own land?");
        copiedQuestions[0].Type.Should().Be(QuestionType.SingleChoice);
        copiedQuestions[0].GetOptions().Should().Equal("Yes", "No");
        copiedQuestions[0].Weight.Should().Be(40);
        copiedQuestions[0].IsRequired.Should().BeTrue();

        copiedQuestions[1].Label.Should().Be("Hectares farmed");
        copiedQuestions[1].MinValue.Should().Be(1m);
        copiedQuestions[1].MaxValue.Should().Be(50m);
    }

    [Fact]
    public async Task Repoints_question_section_references_at_the_copys_own_section()
    {
        var sourceId = await SeedTemplate(status: TemplateStatus.Draft);

        await using var db = _factory.CreateContext();
        var result = await new DuplicateQualificationTemplateHandler(db, _clock).Handle(
            new DuplicateQualificationTemplateCommand(sourceId, Name: null), CancellationToken.None);

        var copy = await LoadTemplate(result.Value);
        var copiedSectionId = copy.Sections.Single().Id;

        copy.Questions.Should().OnlyContain(q => q.SectionId == copiedSectionId);
    }

    [Fact]
    public async Task Rewires_conditional_rules_to_the_copys_own_question_ids()
    {
        var sourceId = await SeedTemplate(status: TemplateStatus.Published);
        var source = await LoadTemplate(sourceId);
        var sourceTriggerId = source.Questions.Single(q => q.Label == "Do you own land?").Id;

        // Guard: the fixture really does carry a rule pointing at a sibling question.
        source.Questions.Single(q => q.Label == "Hectares farmed")
              .GetRules().Single().TriggerQuestionId.Should().Be(sourceTriggerId);

        await using var db = _factory.CreateContext();
        var result = await new DuplicateQualificationTemplateHandler(db, _clock).Handle(
            new DuplicateQualificationTemplateCommand(sourceId, Name: null), CancellationToken.None);

        var copy = await LoadTemplate(result.Value);
        var copiedTriggerId = copy.Questions.Single(q => q.Label == "Do you own land?").Id;
        var copiedRule = copy.Questions.Single(q => q.Label == "Hectares farmed").GetRules().Single();

        // The whole point: a straight RulesJson copy would leave this pointing at the
        // source template's question, and the rule would never fire on the copy.
        copiedRule.TriggerQuestionId.Should().Be(copiedTriggerId);
        copiedRule.TriggerQuestionId.Should().NotBe(sourceTriggerId);
        copiedRule.TriggerValue.Should().Be("Yes");
        copiedRule.Action.Should().Be(QuestionRuleAction.Show);
    }

    [Fact]
    public async Task Uses_the_supplied_name_when_given()
    {
        var sourceId = await SeedTemplate(status: TemplateStatus.Archived);

        await using var db = _factory.CreateContext();
        var result = await new DuplicateQualificationTemplateHandler(db, _clock).Handle(
            new DuplicateQualificationTemplateCommand(sourceId, "  2027 revision  "), CancellationToken.None);

        (await LoadTemplate(result.Value)).Name.Should().Be("2027 revision");
    }

    [Fact]
    public async Task Derived_name_stays_within_the_name_column_length()
    {
        var sourceId = await SeedTemplate(status: TemplateStatus.Draft, name: new string('x', 200));

        await using var db = _factory.CreateContext();
        var result = await new DuplicateQualificationTemplateHandler(db, _clock).Handle(
            new DuplicateQualificationTemplateCommand(sourceId, Name: null), CancellationToken.None);

        var copy = await LoadTemplate(result.Value);
        copy.Name.Should().HaveLength(200).And.EndWith(" (copy)");
    }

    [Fact]
    public async Task Returns_not_found_for_an_unknown_template()
    {
        await using var db = _factory.CreateContext();

        var result = await new DuplicateQualificationTemplateHandler(db, _clock).Handle(
            new DuplicateQualificationTemplateCommand(Guid.NewGuid(), Name: null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("TEMPLATE_NOT_FOUND");
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    /// <summary>
    /// One section holding two questions, the second conditional on the first — the
    /// shape that makes id remapping observable.
    /// </summary>
    private async Task<Guid> SeedTemplate(TemplateStatus status, string name = "Agri loan intake")
    {
        await using var db = _factory.CreateContext();

        var template = QualificationTemplate.Create(
            _tenantId, name, _clock.GetUtcNow(),
            description: "Used at branch intake",
            productCategory: ProductCategory.Loan,
            productCode: "CRED-AGRI-01");

        var section = template.AddSection("Eligibility", "Basic screening").Value;

        template.AddQuestion(
            "Do you own land?", QuestionType.SingleChoice, weight: 40, isRequired: true,
            sectionId: section.Id, options: ["Yes", "No"], helpText: "Title deed or lease");

        var triggerId = template.Questions.Single(q => q.Label == "Do you own land?").Id;

        template.AddQuestion(
            "Hectares farmed", QuestionType.Numeric, weight: 60, isRequired: false,
            sectionId: section.Id, minValue: 1m, maxValue: 50m,
            rules: [(triggerId, "Yes", QuestionRuleAction.Show)]);

        if (status is TemplateStatus.Published or TemplateStatus.Archived)
            template.Publish(_clock.GetUtcNow());

        if (status is TemplateStatus.Archived)
            template.Archive();

        db.QualificationTemplates.Add(template);
        await db.SaveChangesAsync();

        return template.Id;
    }

    private async Task<QualificationTemplate> LoadTemplate(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.QualificationTemplates
            .Include(t => t.Sections)
            .Include(t => t.Questions)
            .SingleAsync(t => t.Id == id);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
