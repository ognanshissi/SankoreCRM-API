namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.TemplateVersion;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Versions de gabarit (propriétaire, 2026-10-26) : un projet suit la version avec laquelle il a été
/// créé ; enregistrer un gabarit utilisé crée la version suivante ; le projet passe à la dernière
/// version sur demande ; les projets d'avant les versions restent sur l'état d'alors.
/// </summary>
public sealed class ProjectTemplateVersionTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public ProjectTemplateVersionTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private static readonly Guid Recueil = Guid.NewGuid();
    private static readonly Guid Devis = Guid.NewGuid();

    private static List<ProjectStepInput> TwoSteps() =>
    [
        new(Recueil, "Recueil", OpportunityStage.Qualification, 20),
        new(Devis, "Devis", OpportunityStage.Proposal, 50),
    ];

    private static List<ProjectStepInput> ThreeSteps() =>
    [
        new(Recueil, "Recueil du besoin", OpportunityStage.Qualification, 25),
        new(null, "Étude", OpportunityStage.NeedsAnalysis, 40),
        new(Devis, "Devis", OpportunityStage.Proposal, 50, RequiresAmount: true),
    ];

    [Fact]
    public async Task Editing_a_template_in_use_leaves_its_projects_on_their_version()
    {
        var template = await CreateTemplateAsync();
        var running = await CreateProjectAsync();

        await UpdateAsync(template, ThreeSteps());

        (await StepsOfAsync(running)).Should().Equal("Recueil", "Devis");
        var fresh = await CreateProjectAsync();
        (await StepsOfAsync(fresh)).Should().Equal("Recueil du besoin", "Étude", "Devis");

        await using var db = _factory.CreateContext();
        (await db.ProjectTemplateVersions.Select(v => v.Number).OrderBy(n => n).ToListAsync()).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_version_no_project_follows_is_rewritten_rather_than_numbered()
    {
        var template = await CreateTemplateAsync();

        await UpdateAsync(template, ThreeSteps());
        await UpdateAsync(template, TwoSteps());

        await using var db = _factory.CreateContext();
        (await db.ProjectTemplateVersions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_project_from_before_versions_keeps_the_template_as_it_was()
    {
        // A template and a project written before versions existed: neither carries one.
        Guid template, project;
        await using (var db = _factory.CreateContext())
        {
            var t = ProjectTemplate.Create(_tenant, "Parcours", null, null, TwoSteps(), DateTimeOffset.UtcNow).Value!;
            var p = Opportunity.CreateFromLead(_tenant, Guid.NewGuid(), "Ancien", "", null, TimeProvider.System);
            db.ProjectTemplates.Add(t);
            db.Opportunities.Add(p);
            await db.SaveChangesAsync();
            (template, project) = (t.Id, p.Id);
        }

        await UpdateAsync(template, ThreeSteps());

        (await StepsOfAsync(project)).Should().Equal("Recueil", "Devis");
        await using var check = _factory.CreateContext();
        var baseline = await check.ProjectTemplateVersions.SingleAsync(v => v.Number == 1);
        baseline.IsBaseline.Should().BeTrue();
        (await check.Opportunities.SingleAsync(o => o.Id == project)).TemplateVersionId.Should().Be(baseline.Id,
            "attached, a template created later for its product no longer pulls it over");
    }

    [Fact]
    public async Task A_project_moves_to_the_latest_version_on_demand_and_keeps_its_step()
    {
        var template = await CreateTemplateAsync();
        var project = await CreateProjectAsync();
        await PlaceOnAsync(project, Devis, OpportunityStage.Proposal);
        await UpdateAsync(template, ThreeSteps());

        (await FollowAsync(project)).IsSuccess.Should().BeTrue();

        (await StepsOfAsync(project)).Should().Equal("Recueil du besoin", "Étude", "Devis");
        await using var db = _factory.CreateContext();
        (await db.Opportunities.SingleAsync(o => o.Id == project)).StepId.Should().Be(Devis);
        (await FollowAsync(project)).Error.Should().Be(FollowLatestTemplateVersionHandler.AlreadyLatest);
    }

    [Fact]
    public async Task A_project_on_the_built_in_default_stays_there_when_the_tenant_saves_one()
    {
        var project = await CreateProjectAsync();
        (await StepsOfAsync(project)).Should().HaveCount(ProjectTemplate.BuiltInDefault.Count);

        await CreateTemplateAsync();

        (await StepsOfAsync(project)).Should().HaveCount(ProjectTemplate.BuiltInDefault.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ProjectTemplateResolver Resolver(LeadsDbContext db) => new(db, _admin, new FixedTenantContext(_tenant));

    private async Task<Guid> CreateTemplateAsync()
    {
        await using var db = _factory.CreateContext();
        var result = await new CreateProjectTemplateHandler(db, _admin, new FixedTenantContext(_tenant), TimeProvider.System)
            .Handle(new CreateProjectTemplateCommand("Parcours", null, null, TwoSteps()), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    private async Task UpdateAsync(Guid template, List<ProjectStepInput> steps)
    {
        await using var db = _factory.CreateContext();
        var result = await new UpdateProjectTemplateHandler(db, TimeProvider.System, templates: Resolver(db))
            .Handle(new UpdateProjectTemplateCommand(template, "Parcours", steps), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
    }

    private async Task<Guid> CreateProjectAsync()
    {
        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateFromLead(_tenant, Guid.NewGuid(), "Obsèques", "", null, TimeProvider.System);
        await ProjectFirstStep.PlaceAsync(Resolver(db), project, DateTimeOffset.UtcNow, CancellationToken.None);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task PlaceOnAsync(Guid project, Guid step, OpportunityStage phase)
    {
        await using var db = _factory.CreateContext();
        var p = await db.Opportunities.AsTracking().SingleAsync(o => o.Id == project);
        p.MoveToStep(step, phase, 50, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<string>> StepsOfAsync(Guid project)
    {
        await using var db = _factory.CreateContext();
        var p = await db.Opportunities.SingleAsync(o => o.Id == project);
        return (await Resolver(db).ForProjectAsync(p, CancellationToken.None)).Steps.Select(s => s.Label).ToList();
    }

    private async Task<Result> FollowAsync(Guid project)
    {
        await using var db = _factory.CreateContext();
        return await new FollowLatestTemplateVersionHandler(db, Resolver(db), TimeProvider.System)
            .Handle(new FollowLatestTemplateVersionCommand(project), CancellationToken.None);
    }
}
