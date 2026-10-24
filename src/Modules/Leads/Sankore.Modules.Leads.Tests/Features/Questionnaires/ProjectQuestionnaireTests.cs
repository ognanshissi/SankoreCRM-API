namespace Sankore.Modules.Leads.Tests.Features.Questionnaires;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Features.Questionnaires.ProjectQuestionnaires;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Les questionnaires vivent dans le PROJET : ce que son produit demande — au dossier et à chaque
/// personne —, ce qui manque, l'étape « Questionnaires complets » et le gain qui le refusent, et la
/// saisie d'une réponse. La conversion du lead ne les regarde plus.
/// </summary>
public sealed class ProjectQuestionnaireTests : IDisposable
{
    private const string Product = "Assurance santé";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _productLine = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ProjectQuestionnaireTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _user.Id.Returns(Guid.NewGuid());
        _user.TenantId.Returns(_tenant);
        _user.Roles.Returns([]);

        _admin.ResolveProductLineAsync(_tenant, Product, Arg.Any<CancellationToken>()).Returns(_productLine);
        _admin.ListProductsAsync(_tenant, null, Arg.Any<CancellationToken>()).Returns(new List<ProductSummary>
        {
            new("SANTE-01", Product, nameof(ProductCategory.HealthInsurance), true, _productLine),
        });
        // No KYC asked of this line, so the gate tests only see the questionnaires.
        _admin.GetBeneficiaryPolicyAsync(_tenant, _productLine, Arg.Any<CancellationToken>())
            .Returns(new BeneficiaryPolicyDto(
                _productLine, "SANTE-01", nameof(ProductCategory.HealthInsurance),
                true, false, [], null, null, [], null, null));
        _admin.GetComplianceSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TestCompleteness.FatcaOff);
    }

    public void Dispose() => _factory.Dispose();

    // ── Ce qui manque ─────────────────────────────────────────────────────

    [Fact]
    public async Task Every_person_who_has_not_answered_is_reported_with_a_label()
    {
        var (lead, project) = await SeedAsync(HealthTemplate());

        await using var db = _factory.CreateContext();
        var missing = await MissingAsync(db, project);

        missing.Should().HaveCount(2);
        missing.Select(m => m.SubjectLabel).Should().BeEquivalentTo(["Le titulaire", "L'enfant Kofi"]);
        missing.Should().OnlyContain(m => m.IsHealthQuestionnaire);
        _ = lead;
    }

    [Fact]
    public async Task The_file_questionnaire_is_reported_as_the_file()
    {
        var (_, project) = await SeedAsync(DossierTemplate());

        await using var db = _factory.CreateContext();
        var missing = await MissingAsync(db, project);

        missing.Should().ContainSingle().Which.SubjectLabel.Should().Be(QuestionnaireSubjectLabel.Dossier);
    }

    [Fact]
    public async Task A_removed_member_owes_nothing()
    {
        var (lead, project) = await SeedAsync(HealthTemplate());

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking().Include(l => l.HouseholdMembers).SingleAsync();
            tracked.RemoveHouseholdMember(tracked.HouseholdMembers[0].Id, "sorti du foyer", Now);
            await db.SaveChangesAsync();
        }

        await using var read = _factory.CreateContext();
        (await MissingAsync(read, project)).Should().ContainSingle()
            .Which.SubjectType.Should().Be(nameof(QualificationSubjectType.Principal));
        _ = lead;
    }

    [Fact]
    public async Task Answers_given_on_the_lead_count_for_its_projects()
    {
        // Before projects held questionnaires, or captured with the lead: a row with no project.
        var template = HealthTemplate();
        var (lead, project) = await SeedAsync(template);

        await using (var db = _factory.CreateContext())
        {
            var childId = (await db.Leads.Include(l => l.HouseholdMembers).SingleAsync()).HouseholdMembers[0].Id;
            foreach (var (type, subject) in new[]
                     {
                         (QualificationSubjectType.Principal, lead.Id),
                         (QualificationSubjectType.HouseholdMember, childId),
                     })
                db.QualificationResponses.Add(Answered(template, lead.Id, opportunityId: null, type, subject));
            await db.SaveChangesAsync();
        }

        await using var read = _factory.CreateContext();
        (await MissingAsync(read, project)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_answer_given_on_one_project_does_not_serve_another()
    {
        var template = DossierTemplate();
        var (lead, project) = await SeedAsync(template);
        Guid other;

        await using (var db = _factory.CreateContext())
        {
            var second = Opportunity.CreateFromLead(_tenant, lead.Id, "Autre projet", Product, null, TimeProvider.System);
            db.Opportunities.Add(second);
            db.QualificationResponses.Add(Answered(template, lead.Id, project, QualificationSubjectType.Lead, lead.Id));
            await db.SaveChangesAsync();
            other = second.Id;
        }

        await using var read = _factory.CreateContext();
        (await MissingAsync(read, project)).Should().BeEmpty();
        (await MissingAsync(read, other)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_lead_conversion_no_longer_asks_for_questionnaires()
    {
        var (_, _) = await SeedAsync(HealthTemplate());

        await using var db = _factory.CreateContext();
        var lead = await db.Leads.Include(l => l.HouseholdMembers).SingleAsync();
        var report = await TestCompleteness.Calculator(db, _admin, _customers).ComputeAsync(lead, CancellationToken.None);

        report.Gaps.Should().NotContain(g => g.Code == "MISSING_QUESTIONNAIRE");
    }

    // ── L'étape marquée et le gain ────────────────────────────────────────

    [Fact]
    public async Task The_marked_step_is_refused_while_a_questionnaire_is_unanswered()
    {
        var (_, project) = await SeedAsync(HealthTemplate());
        var steps = await SeedTemplateAsync(markQuestionnaires: true);

        var result = await MoveAsync(project, steps[1]);

        result.Error.Should().Be(ProjectTemplateErrors.QuestionnairesIncomplete);
    }

    [Fact]
    public async Task A_step_before_the_marked_one_is_not_gated()
    {
        var (_, project) = await SeedAsync(HealthTemplate());
        var steps = await SeedTemplateAsync(markQuestionnaires: true, markedIndex: 2);

        (await MoveAsync(project, steps[1])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_marked_step_passes_once_everything_is_answered()
    {
        var template = DossierTemplate();
        var (lead, project) = await SeedAsync(template);
        var steps = await SeedTemplateAsync(markQuestionnaires: true);

        await using (var db = _factory.CreateContext())
        {
            db.QualificationResponses.Add(Answered(template, lead.Id, project, QualificationSubjectType.Lead, lead.Id));
            await db.SaveChangesAsync();
        }

        (await MoveAsync(project, steps[1])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_unreadable_catalogue_refuses_the_marked_step()
    {
        var (_, project) = await SeedAsync(HealthTemplate());
        var steps = await SeedTemplateAsync(markQuestionnaires: true);
        // The product line's policy is what the questionnaire plan reads; the step template comes
        // from the catalogue list, which still answers.
        _admin.GetBeneficiaryPolicyAsync(_tenant, _productLine, Arg.Any<CancellationToken>())
            .Returns<Task<BeneficiaryPolicyDto?>>(_ => throw new HttpRequestException("M12 down"));

        var result = await MoveAsync(project, steps[1]);

        result.Error.Should().Be(ProjectTemplateErrors.QuestionnairesIncomplete);
    }

    [Fact]
    public async Task Winning_needs_the_per_person_declarations_even_with_no_marked_step()
    {
        var (_, project) = await SeedAsync(HealthTemplate());

        var result = await CloseAsync(project, OpportunityStage.ClosedWon);

        result.Error.Should().Be(ProjectTemplateErrors.QuestionnairesIncomplete);
    }

    [Fact]
    public async Task Winning_with_no_marked_step_does_not_need_the_file_questionnaire()
    {
        var (_, project) = await SeedAsync(DossierTemplate());

        var result = await CloseAsync(project, OpportunityStage.ClosedWon);

        result.Error.Should().NotBe(ProjectTemplateErrors.QuestionnairesIncomplete);
    }

    // ── La saisie ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_file_answer_is_recorded_on_the_project_with_its_score()
    {
        var template = DossierTemplate();
        var (lead, project) = await SeedAsync(template);

        var result = await SubmitAsync(project, template, QualificationSubjectType.Lead, lead.Id, "true");

        result.IsSuccess.Should().BeTrue(result.Error);
        await using var db = _factory.CreateContext();
        var response = await db.QualificationResponses.SingleAsync(r => r.Status == QualificationResponseStatus.Completed);
        response.OpportunityId.Should().Be(project);
        response.LeadId.Should().Be(lead.Id);
        response.ComputedScore.Should().Be(100);
    }

    [Fact]
    public async Task A_template_the_product_does_not_ask_is_refused()
    {
        var asked = DossierTemplate();
        var other = DossierTemplate(ProductCategory.Loan);
        var (lead, project) = await SeedAsync(asked, other);

        var result = await SubmitAsync(project, other, QualificationSubjectType.Lead, lead.Id, "true");

        result.Error.Should().Be(SubmitProjectQuestionnaireResponseHandler.NotApplicable);
    }

    [Fact]
    public async Task A_health_answer_without_consent_is_refused()
    {
        var template = HealthTemplate();
        var (lead, project) = await SeedAsync(template);

        var result = await SubmitAsync(project, template, QualificationSubjectType.Principal, lead.Id, "false");

        result.Error.Should().Be(HealthConsentGuard.Missing);
    }

    [Fact]
    public async Task A_required_question_left_blank_is_refused()
    {
        var template = DossierTemplate();
        var (lead, project) = await SeedAsync(template);

        var result = await SubmitAsync(project, template, QualificationSubjectType.Lead, lead.Id, " ");

        result.Error.Should().Be(SubmitProjectQuestionnaireResponseHandler.RequiredNotAnswered);
    }

    [Fact]
    public async Task Correcting_on_the_project_leaves_the_lead_answer_alone()
    {
        // The answer on the lead may serve other projects: the correction belongs to this one.
        var template = DossierTemplate();
        var (lead, project) = await SeedAsync(template);
        await using (var db = _factory.CreateContext())
        {
            db.QualificationResponses.Add(Answered(template, lead.Id, opportunityId: null, QualificationSubjectType.Lead, lead.Id));
            await db.SaveChangesAsync();
        }

        (await SubmitAsync(project, template, QualificationSubjectType.Lead, lead.Id, "false")).IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var rows = await read.QualificationResponses.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(r => r.OpportunityId == null).GetAnswers().Single().Value.Should().Be("true");
        rows.Single(r => r.OpportunityId == project).GetAnswers().Single().Value.Should().Be("false");
    }

    [Fact]
    public async Task Reading_lists_the_file_and_each_person_with_their_status()
    {
        var (lead, project) = await SeedAsync(DossierTemplate(), HealthTemplate());

        await using var db = _factory.CreateContext();
        var result = await new GetProjectQuestionnairesHandler(
                db, Guard(), Checker(db), TimeProvider.System)
            .Handle(new GetProjectQuestionnairesQuery(project, IncludeHealthAnswers: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Items.Should().HaveCount(3);
        result.Value.Items.Should().ContainSingle(i => i.IsDossier);
        result.Value.Items.Should().OnlyContain(i => i.Status == "ToComplete");
        result.Value.IsComplete.Should().BeFalse();
        result.Value.Respondents.Should().Contain(r => r.Id == lead.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private QualificationTemplate HealthTemplate()
    {
        var template = QualificationTemplate.Create(
            _tenant, "Questionnaire santé", Now, productCategory: ProductCategory.HealthInsurance);
        template.AddQuestion("Fumez-vous ?", QuestionType.YesNo, 10, true);
        template.SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false);
        template.Publish(Now);
        return template;
    }

    private QualificationTemplate DossierTemplate(ProductCategory category = ProductCategory.HealthInsurance)
    {
        var template = QualificationTemplate.Create(_tenant, "Qualification", Now, productCategory: category);
        template.AddQuestion("Budget validé ?", QuestionType.YesNo, 10, true);
        template.Publish(Now);
        return template;
    }

    /// <summary>A lead with one child, owned by the caller, and one open project on the product.</summary>
    private async Task<(Lead Lead, Guid Project)> SeedAsync(params QualificationTemplate[] templates)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: Product,
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: Guid.NewGuid(),
            clock: TimeProvider.System, firstName: "Aya", lastName: "Kouassi",
            ownerId: _user.Id);
        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, PersonIdentity.Create("Kouassi", "Kofi"), null, true, Now);

        var project = Opportunity.CreateFromLead(_tenant, lead.Id, "Projet santé", Product, null, TimeProvider.System);

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.Opportunities.Add(project);
        db.QualificationTemplates.AddRange(templates);
        await db.SaveChangesAsync();
        return (lead, project.Id);
    }

    private async Task<List<Guid>> SeedTemplateAsync(bool markQuestionnaires, int markedIndex = 1)
    {
        var steps = new List<ProjectStepInput>
        {
            new(null, "Découverte", OpportunityStage.Prospecting, 10),
            new(null, "Étude", OpportunityStage.NeedsAnalysis, 40, RequiresQuestionnaires: markQuestionnaires && markedIndex == 1),
            new(null, "Offre", OpportunityStage.Proposal, 60, RequiresQuestionnaires: markQuestionnaires && markedIndex == 2),
        };
        var template = ProjectTemplate.Create(_tenant, "Santé", ProductCategory.HealthInsurance, null, steps, Now).Value;

        await using var db = _factory.CreateContext();
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Steps.Select(s => s.Id).ToList();
    }

    private QualificationResponse Answered(
        QualificationTemplate template, Guid leadId, Guid? opportunityId,
        QualificationSubjectType type, Guid subject)
        => QualificationResponse.Create(
            tenantId: _tenant, leadId: leadId, templateId: template.Id,
            answeredBy: Guid.NewGuid(), clock: TimeProvider.System,
            answers: [(template.Questions[0].Id, "true")], computedScore: 0,
            questionnaireVersion: template.Version,
            subjectType: type, subjectId: subject, isHealthData: template.IsHealthQuestionnaire,
            opportunityId: opportunityId);

    private QuestionnaireCompletenessChecker Checker(LeadsDbContext db)
        => new(db, new ExpectedResponsePlanner(db, _admin, TimeProvider.System));

    private LeadAccessGuard Guard() => new(_user, _admin);

    private async Task<IReadOnlyList<MissingQuestionnaire>> MissingAsync(LeadsDbContext db, Guid projectId)
    {
        var project = await db.Opportunities.SingleAsync(o => o.Id == projectId);
        var checker = Checker(db);
        return await checker.MissingAsync(project, await checker.LeadOfAsync(project, CancellationToken.None), CancellationToken.None);
    }

    private ProjectStepGate Gate(LeadsDbContext db) => new(
        db, _admin, Substitute.For<IKycModule>(), new FixedTenantContext(_tenant), _user,
        Substitute.For<IEventPublisher>(), Checker(db));

    private async Task<Result> MoveAsync(Guid projectId, Guid stepId)
    {
        await using var db = _factory.CreateContext();
        return await new MoveOpportunityToStepHandler(
                db, new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenant)), TimeProvider.System, Gate(db))
            .Handle(new MoveOpportunityToStepCommand(projectId, stepId), CancellationToken.None);
    }

    private async Task<Result> CloseAsync(Guid projectId, OpportunityStage outcome)
    {
        await using var db = _factory.CreateContext();
        var result = await new CloseOpportunityHandler(
                db, _customers, _admin, _user, TimeProvider.System, NullLogger<CloseOpportunityHandler>.Instance,
                new ProjectTemplateResolver(db, _admin, new FixedTenantContext(_tenant)), Gate(db))
            .Handle(new CloseOpportunityCommand(projectId, outcome, "motif", null), CancellationToken.None);
        return result.IsSuccess ? Result.Ok() : Result.Fail(result.Error!);
    }

    private async Task<Result> SubmitAsync(
        Guid projectId, QualificationTemplate template, QualificationSubjectType type, Guid subject, string value)
    {
        await using var db = _factory.CreateContext();
        return await new SubmitProjectQuestionnaireResponseHandler(
                db, Guard(), new HealthConsentGuard(db), Checker(db),
                new ExpectedResponsePlanner(db, _admin, TimeProvider.System), _user, TimeProvider.System)
            .Handle(new SubmitProjectQuestionnaireResponseCommand(
                projectId, template.Id, type.ToString(), subject,
                [new QuestionnaireAnswerInput(template.Questions[0].Id, value)]), CancellationToken.None);
    }
}
