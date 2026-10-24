namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Les gabarits d'étapes de projet : les règles d'un gabarit, la résolution à trois niveaux
/// (produit, famille, défaut — puis le défaut intégré), et le déplacement d'un projet.
///
/// <para>
/// Ce qui est épinglé en priorité : un projet existant ne bouge pas quand les gabarits changent.
/// Il garde son étape tant que son gabarit l'a, et retombe sinon sur l'étape de sa phase.
/// </para>
/// </summary>
public sealed class ProjectTemplateTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T09:00:00Z");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _creditLineId = Guid.NewGuid();
    private readonly Guid _savingsLineId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly TimeProvider _clock = new FixedClock(Now);

    public ProjectTemplateTests()
    {
        _factory = new TestDbContextFactory(_tenantId);

        _administration.ResolveProductLineAsync(_tenantId, "CREDCOM", Arg.Any<CancellationToken>())
            .Returns(_creditLineId);
        _administration.ResolveProductLineAsync(_tenantId, "EPARGNE", Arg.Any<CancellationToken>())
            .Returns(_savingsLineId);
        _administration.ListProductsAsync(_tenantId, null, Arg.Any<CancellationToken>())
            .Returns(new List<ProductSummary>
            {
                new("CREDCOM", "Crédit commerçant", "Loan", true, _creditLineId),
                new("EPARGNE", "Épargne plus", "Savings", true, _savingsLineId),
            });
        _administration.GetProductAsync(_tenantId, "CREDCOM", Arg.Any<CancellationToken>())
            .Returns(new ProductSummary("CREDCOM", "Crédit commerçant", "Loan", true, _creditLineId));
    }

    public void Dispose() => _factory.Dispose();

    // ── Les règles d'un gabarit ─────────────────────────────────────────────

    [Fact]
    public void Phases_never_go_backwards_along_the_steps()
    {
        // Les rapports lisent encore la phase : un gabarit qui repasserait de Proposition à Étude
        // ferait reculer l'entonnoir d'un projet qui avance.
        var result = ProjectTemplate.Create(_tenantId, "Crédit", ProductCategory.Loan, null,
        [
            Step("Montage", OpportunityStage.Proposal, 60),
            Step("Analyse", OpportunityStage.NeedsAnalysis, 70),
        ], Now);

        result.Error.Should().Be(ProjectTemplateErrors.StepPhasesOutOfOrder);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void An_open_step_is_neither_certain_nor_lost(int probability)
    {
        var result = ProjectTemplate.Create(_tenantId, "Crédit", ProductCategory.Loan, null,
            [Step("Accord", OpportunityStage.Negotiation, probability)], Now);

        result.Error.Should().Be(ProjectTemplateErrors.StepProbabilityOutOfRange);
    }

    [Fact]
    public void Two_steps_cannot_share_a_label()
    {
        var result = ProjectTemplate.Create(_tenantId, "Crédit", ProductCategory.Loan, null,
        [
            Step("Étude", OpportunityStage.NeedsAnalysis, 30),
            Step("étude", OpportunityStage.NeedsAnalysis, 40),
        ], Now);

        result.Error.Should().Be(ProjectTemplateErrors.StepLabelDuplicate);
    }

    [Fact]
    public void A_template_is_scoped_to_a_family_or_a_product_never_both()
    {
        var result = ProjectTemplate.Create(_tenantId, "Crédit", ProductCategory.Loan, "CREDCOM",
            [Step("Étude", OpportunityStage.NeedsAnalysis, 30)], Now);

        result.Error.Should().Be(ProjectTemplateErrors.ScopeInvalid);
    }

    [Fact]
    public async Task Saving_steps_keeps_the_ids_sent_back_and_drops_the_others()
    {
        var id = await SeedTemplate("Crédit", ProductCategory.Loan, null,
            Step("Étude", OpportunityStage.NeedsAnalysis, 30),
            Step("Comité", OpportunityStage.Negotiation, 80));

        var before = await LoadTemplate(id);
        var kept = before.Steps[0];

        var result = await Update(id, "Crédit",
        [
            new ProjectStepInput(kept.Id, "Étude du dossier", OpportunityStage.NeedsAnalysis, 35),
            Step("Accord", OpportunityStage.Negotiation, 90),
        ]);

        result.IsSuccess.Should().BeTrue();
        var after = await LoadTemplate(id);
        after.Steps.Select(s => s.Label).Should().Equal("Étude du dossier", "Accord");
        after.Steps[0].Id.Should().Be(kept.Id);
        after.Steps.Should().NotContain(s => s.Id == before.Steps[1].Id);
    }

    // ── La résolution ─────────────────────────────────────────────────────

    [Fact]
    public async Task Without_any_template_a_project_gets_the_built_in_default()
    {
        var resolved = await Resolver().ResolveAsync("CREDCOM", CancellationToken.None);

        resolved.Id.Should().BeNull();
        resolved.Scope.Should().Be("Default");
        resolved.Steps.Select(s => s.Label)
            .Should().Equal("Prospection", "Qualification", "Étude", "Proposition", "Négociation");
    }

    [Fact]
    public async Task A_product_template_beats_its_family_which_beats_the_default()
    {
        var def = await SeedTemplate("Défaut", null, null, Step("Contact", OpportunityStage.Prospecting, 10));
        var family = await SeedTemplate("Crédit", ProductCategory.Loan, null, Step("Étude", OpportunityStage.NeedsAnalysis, 30));

        (await Resolver().ResolveAsync("CREDCOM", CancellationToken.None)).Id.Should().Be(family);
        (await Resolver().ResolveAsync("EPARGNE", CancellationToken.None)).Id.Should().Be(def);
        (await Resolver().ResolveAsync("", CancellationToken.None)).Id.Should().Be(def);

        var product = await SeedTemplate("Crédit commerçant", null, "credcom", Step("Visite", OpportunityStage.Qualification, 25));
        (await Resolver().ResolveAsync("CREDCOM", CancellationToken.None)).Id.Should().Be(product);
    }

    [Fact]
    public void A_project_moved_by_phase_stands_on_the_last_step_not_past_its_phase()
    {
        var template = new ResolvedProjectTemplate(null, "Crédit", "Category",
        [
            new ProjectStepDto(Guid.NewGuid(), "Contact", OpportunityStage.Prospecting, 10, null),
            new ProjectStepDto(Guid.NewGuid(), "Pièces", OpportunityStage.NeedsAnalysis, 40, null),
            new ProjectStepDto(Guid.NewGuid(), "Comité", OpportunityStage.Negotiation, 80, null),
        ]);

        // Proposition n'a pas d'étape ici : le projet reste sur « Pièces », il ne monte pas à « Comité ».
        template.CurrentStep(null, OpportunityStage.Proposal)!.Label.Should().Be("Pièces");
        template.CurrentStep(Guid.NewGuid(), OpportunityStage.Prospecting)!.Label.Should().Be("Contact");
        template.CurrentStep(null, OpportunityStage.ClosedWon).Should().BeNull();
    }

    // ── Le déplacement d'un projet ──────────────────────────────────────────

    [Fact]
    public async Task A_project_moves_onto_a_step_of_its_template_and_takes_its_phase_and_probability()
    {
        await SeedTemplate("Crédit", ProductCategory.Loan, null,
            Step("Pièces", OpportunityStage.NeedsAnalysis, 45),
            Step("Comité", OpportunityStage.Negotiation, 85));
        var resolved = await Resolver().ResolveAsync("CREDCOM", CancellationToken.None);
        var comite = resolved.Steps[1];
        var projectId = await SeedProject("CREDCOM");

        var result = await Move(projectId, comite.Id);

        result.IsSuccess.Should().BeTrue();
        var project = await LoadProject(projectId);
        project.StepId.Should().Be(comite.Id);
        project.Stage.Should().Be(OpportunityStage.Negotiation);
        project.Probability.Should().Be(0.85);
    }

    [Fact]
    public async Task A_step_of_another_template_is_refused()
    {
        await SeedTemplate("Crédit", ProductCategory.Loan, null, Step("Comité", OpportunityStage.Negotiation, 85));
        var projectId = await SeedProject("EPARGNE");

        var result = await Move(projectId, Guid.NewGuid());

        result.Error.Should().Be(ProjectTemplateErrors.StepNotInTemplate);
        (await LoadProject(projectId)).StepId.Should().BeNull();
    }

    [Fact]
    public async Task The_built_in_default_steps_can_be_used_before_any_template_is_saved()
    {
        var projectId = await SeedProject("");
        var etude = ProjectTemplate.BuiltInDefault[2].Id!.Value;

        (await Move(projectId, etude)).IsSuccess.Should().BeTrue();

        var project = await LoadProject(projectId);
        project.Stage.Should().Be(OpportunityStage.NeedsAnalysis);
        project.Probability.Should().Be(0.40);
    }

    [Fact]
    public async Task Moving_by_phase_forgets_the_step()
    {
        var projectId = await SeedProject("");
        await Move(projectId, ProjectTemplate.BuiltInDefault[2].Id!.Value);

        await using (var db = _factory.CreateContext())
        {
            await new AdvanceOpportunityStageHandler(db, _clock).Handle(
                new AdvanceOpportunityStageCommand(projectId, OpportunityStage.Proposal), CancellationToken.None);
        }

        (await LoadProject(projectId)).StepId.Should().BeNull();
    }

    [Fact]
    public async Task Projects_carry_their_template_steps_and_the_step_they_stand_on()
    {
        await SeedTemplate("Crédit", ProductCategory.Loan, null,
            Step("Contact", OpportunityStage.Prospecting, 10),
            Step("Comité", OpportunityStage.Negotiation, 85));
        var projectId = await SeedProject("CREDCOM");

        await using var db = _factory.CreateContext();
        var list = await new Sankore.Modules.Leads.Features.Opportunities.ListOpportunities.ListOpportunitiesHandler(
                db, templates: new ProjectTemplateResolver(db, _administration, new FixedTenantContext(_tenantId)))
            .Handle(new Sankore.Modules.Leads.Features.Opportunities.ListOpportunities.ListOpportunitiesQuery(), CancellationToken.None);

        var dto = list.Value!.Single(o => o.Id == projectId);
        dto.TemplateName.Should().Be("Crédit");
        dto.Steps!.Select(s => s.Label).Should().Equal("Contact", "Comité");
        dto.CurrentStepId.Should().Be(dto.Steps![0].Id);
    }

    [Fact]
    public async Task Each_project_says_who_it_is_for_so_the_board_can_search_it()
    {
        var clientId = Guid.NewGuid();
        Guid leadProject, clientProject;
        await using (var db = _factory.CreateContext())
        {
            var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
            db.Leads.Add(lead);
            var fromLead = Opportunity.CreateFromLead(_tenantId, lead.Id, "Projet lead", "", null, _clock);
            var onClient = Opportunity.CreateForCustomer(_tenantId, clientId, "Projet client", "EPARGNE", null, _clock);
            db.Opportunities.AddRange(fromLead, onClient);
            await db.SaveChangesAsync();
            (leadProject, clientProject) = (fromLead.Id, onClient.Id);
        }

        var customers = Substitute.For<Sankore.Modules.Customers.PublicApi.ICustomersModule>();
        customers.GetClientSummariesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Sankore.Modules.Customers.PublicApi.ClientSummary>
            {
                [clientId] = new(clientId, "CL-0042", "Individual", "Awa Koné", "Active", Guid.NewGuid(), null, "Approved", "Low", null),
            });

        await using var read = _factory.CreateContext();
        var list = (await new Sankore.Modules.Leads.Features.Opportunities.ListOpportunities.ListOpportunitiesHandler(
                read, customers: customers, tenant: new FixedTenantContext(_tenantId))
            .Handle(new Sankore.Modules.Leads.Features.Opportunities.ListOpportunities.ListOpportunitiesQuery(), CancellationToken.None)).Value!;

        var lead1 = list.Single(o => o.Id == leadProject).Contact!;
        lead1.PhoneNumber.Should().NotBeNullOrEmpty();
        lead1.Name.Should().NotBeNullOrEmpty();
        list.Single(o => o.Id == clientProject).Contact.Should().Be(
            new ProjectContactDto("Awa Koné", null, null, null, null, "CL-0042"));
    }

    // ── Les commandes de gabarit ────────────────────────────────────────────

    [Fact]
    public async Task One_template_per_scope()
    {
        await SeedTemplate("Crédit", ProductCategory.Loan, null, Step("Étude", OpportunityStage.NeedsAnalysis, 30));

        await using var db = _factory.CreateContext();
        var result = await new CreateProjectTemplateHandler(db, _administration, new FixedTenantContext(_tenantId), _clock)
            .Handle(new CreateProjectTemplateCommand("Crédit bis", ProductCategory.Loan, null,
                [Step("Étude", OpportunityStage.NeedsAnalysis, 30)]), CancellationToken.None);

        result.Error.Should().Be(ProjectTemplateErrors.ScopeTaken);
    }

    [Fact]
    public async Task A_product_template_names_a_product_of_the_catalogue()
    {
        await using var db = _factory.CreateContext();
        var result = await new CreateProjectTemplateHandler(db, _administration, new FixedTenantContext(_tenantId), _clock)
            .Handle(new CreateProjectTemplateCommand("Inconnu", null, "NOPE",
                [Step("Étude", OpportunityStage.NeedsAnalysis, 30)]), CancellationToken.None);

        result.Error.Should().Be(ProjectTemplateErrors.UnknownProduct);
    }

    [Fact]
    public async Task The_list_starts_with_the_built_in_default_until_one_is_saved()
    {
        await SeedTemplate("Crédit", ProductCategory.Loan, null, Step("Étude", OpportunityStage.NeedsAnalysis, 30));

        await using var db = _factory.CreateContext();
        var list = (await new ListProjectTemplatesHandler(db, _administration, new FixedTenantContext(_tenantId))
            .Handle(new ListProjectTemplatesQuery(), CancellationToken.None)).Value!;

        list.Select(t => t.Scope).Should().Equal("Default", "Category");
        list[0].IsBuiltIn.Should().BeTrue();
        list[0].Id.Should().BeNull();
    }

    // ── Outils ──────────────────────────────────────────────────────────────

    private static ProjectStepInput Step(string label, OpportunityStage phase, int probability)
        => new(null, label, phase, probability);

    private ProjectTemplateResolver Resolver()
        => new(_factory.CreateContext(), _administration, new FixedTenantContext(_tenantId));

    private async Task<Guid> SeedTemplate(string name, ProductCategory? category, string? code, params ProjectStepInput[] steps)
    {
        await using var db = _factory.CreateContext();
        var template = ProjectTemplate.Create(_tenantId, name, category, code, steps, Now).Value!;
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }

    private async Task<Result> Update(Guid id, string name, IReadOnlyList<ProjectStepInput> steps)
    {
        await using var db = _factory.CreateContext();
        return await new UpdateProjectTemplateHandler(db, _clock)
            .Handle(new UpdateProjectTemplateCommand(id, name, steps), CancellationToken.None);
    }

    private async Task<ProjectTemplate> LoadTemplate(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.ProjectTemplates.SingleAsync(t => t.Id == id);
    }

    private async Task<Guid> SeedProject(string product)
    {
        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateFromLead(_tenantId, Guid.NewGuid(), "Projet", product, null, _clock);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Result> Move(Guid projectId, Guid stepId)
    {
        await using var db = _factory.CreateContext();
        return await new MoveOpportunityToStepHandler(
                db, new ProjectTemplateResolver(db, _administration, new FixedTenantContext(_tenantId)), new ProjectStepTransition(_clock))
            .Handle(new MoveOpportunityToStepCommand(projectId, stepId), CancellationToken.None);
    }

    private async Task<Opportunity> LoadProject(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Opportunities.SingleAsync(o => o.Id == id);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
