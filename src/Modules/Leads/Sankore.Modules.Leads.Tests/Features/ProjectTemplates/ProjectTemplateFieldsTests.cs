namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.Opportunities.ProjectFields;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Les champs d'un gabarit de projet (propriétaire, 2026-10-26) : déclarés dans le gabarit, saisis
/// sur le projet, exigés par une étape pour l'atteindre ; et le marqueur « Questionnaires remplis »,
/// refusé tant qu'aucun formulaire de qualification publié ne s'applique.
/// </summary>
public sealed class ProjectTemplateFieldsTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private List<Guid> _steps = [];

    public ProjectTemplateFieldsTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _user.Id.Returns(Guid.NewGuid());
        _user.TenantId.Returns(_tenant);
    }

    public void Dispose() => _factory.Dispose();

    // ── The template ─────────────────────────────────────────────────────

    [Fact]
    public void A_field_gets_a_key_from_its_label_and_keeps_it_when_renamed()
    {
        var template = ProjectTemplate.Create(_tenant, "Obsèques", null, null, Steps(),
            DateTimeOffset.UtcNow, fields: [new(null, "Nombre de personnes couvertes", ProjectFieldType.Integer)]).Value!;

        template.Fields.Single().Key.Should().Be("nombreDePersonnesCouvertes");

        template.Update("Obsèques", Steps(), DateTimeOffset.UtcNow, null,
            [new("nombreDePersonnesCouvertes", "Personnes couvertes", ProjectFieldType.Integer)]).IsSuccess.Should().BeTrue();

        template.Fields.Single().Should().BeEquivalentTo(
            new ProjectFieldDefinition("nombreDePersonnesCouvertes", "Personnes couvertes", ProjectFieldType.Integer));
    }

    [Fact]
    public void A_new_field_keeps_the_key_the_screen_chose_so_a_step_can_require_it()
    {
        var template = ProjectTemplate.Create(_tenant, "P", null, null, Steps(requires: "devisSigne"), DateTimeOffset.UtcNow,
            fields: [new("devisSigne", "Devis signé", ProjectFieldType.Attachment)]);

        template.IsSuccess.Should().BeTrue(template.Error);
        template.Value!.Fields.Single().Key.Should().Be("devisSigne");
    }

    [Fact]
    public void A_saved_field_cannot_change_type()
    {
        // Values typed as a date must not be re-read as an amount.
        var template = ProjectTemplate.Create(_tenant, "P", null, null, Steps(), DateTimeOffset.UtcNow,
            fields: [new(null, "Date du décès", ProjectFieldType.Date)]).Value!;

        template.Update("P", Steps(), DateTimeOffset.UtcNow, null,
                [new("dateDuDeces", "Date du décès", ProjectFieldType.Money)])
            .Error.Should().Be(ProjectTemplateErrors.FieldTypeChanged);
    }

    [Fact]
    public void A_choice_field_needs_options_and_a_step_only_requires_a_declared_field()
    {
        ProjectTemplate.Create(_tenant, "P", null, null, Steps(), DateTimeOffset.UtcNow,
                fields: [new(null, "Formule", ProjectFieldType.SingleChoice)])
            .Error.Should().Be(ProjectTemplateErrors.FieldOptionsRequired);

        ProjectTemplate.Create(_tenant, "P", null, null, Steps(requires: "inconnu"), DateTimeOffset.UtcNow)
            .Error.Should().Be(ProjectTemplateErrors.StepFieldUnknown);
    }

    // ── The step gate ────────────────────────────────────────────────────

    [Fact]
    public async Task Reaching_a_step_needs_its_required_fields_filled()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync();

        (await MoveAsync(project, _steps[1])).Error.Should().Be(ProjectTemplateErrors.FieldsRequired);

        (await SetAsync(project, new() { ["nombreDePersonnes"] = Json("3") })).IsSuccess.Should().BeTrue();
        (await MoveAsync(project, _steps[1])).Error.Should().Be(ProjectTemplateErrors.FieldsRequired,
            "the estimate file is still missing");

        await using (var db = _factory.CreateContext())
        {
            db.ProjectAttachments.Add(ProjectAttachment.Record(
                _tenant, project, "devisSigne", "devis.pdf", "application/pdf", 10, "x", "ref", _user.Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        (await MoveAsync(project, _steps[1])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Values_are_checked_against_the_field_type_and_files_are_not_values()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync();

        (await SetAsync(project, new() { ["nombreDePersonnes"] = Json("\"trois\"") })).IsFailure.Should().BeTrue();
        (await SetAsync(project, new() { ["devisSigne"] = Json("\"x\"") })).Error.Should().StartWith("CUSTOM_FIELD_UNKNOWN");

        (await SetAsync(project, new() { ["nombreDePersonnes"] = Json("4") })).IsSuccess.Should().BeTrue();
        await using var db = _factory.CreateContext();
        (await db.Opportunities.SingleAsync(o => o.Id == project)).CustomFieldsJson.Should().Contain("\"nombreDePersonnes\":4");
    }

    [Fact]
    public async Task A_file_goes_only_to_an_attachment_field()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync();

        await using var db = _factory.CreateContext();
        var handlers = new ProjectAttachmentHandlers(
            db, Resolver(db), _user, new ServiceCollectionProvider(), Options.Create(new TaskAttachmentStorageOptions()),
            TimeProvider.System, NullLogger<ProjectAttachmentHandlers>.Instance);

        (await handlers.Handle(new StoreProjectAttachment(project, "nombreDePersonnes", "a.pdf", [1]), CancellationToken.None))
            .Error.Should().Be(ProjectAttachmentHandlers.FieldNotAttachment);
        (await handlers.Handle(new StoreProjectAttachment(project, "devisSigne", "a.pdf", [1]), CancellationToken.None))
            .Error.Should().Be(ProjectAttachmentHandlers.Unavailable, "no store is registered without a key");
    }

    // ── The questionnaire marker ─────────────────────────────────────────

    [Fact]
    public async Task The_questionnaire_marker_needs_a_published_qualification_form()
    {
        var steps = new List<ProjectStepInput> { new(null, "Souscription", OpportunityStage.Negotiation, 70, RequiresQuestionnaires: true) };

        (await CreateAsync(steps)).Error.Should().Be(ProjectTemplateErrors.QuestionnaireFormMissing);

        await using (var db = _factory.CreateContext())
        {
            var form = QualificationTemplate.Create(_tenant, "Questionnaire crédit", DateTimeOffset.UtcNow, productCategory: ProductCategory.Loan);
            form.AddQuestion("Situation familiale ?", QuestionType.YesNo, weight: 1, isRequired: false).IsSuccess.Should().BeTrue();
            form.Publish(DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
            db.QualificationTemplates.Add(form);
            await db.SaveChangesAsync();
        }

        (await CreateAsync(steps)).IsSuccess.Should().BeTrue();
        (await CreateAsync(steps, category: null)).Error.Should().Be(ProjectTemplateErrors.QuestionnaireFormMissing,
            "the default template names no product, so no form can be checked for it");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<ProjectStepInput> Steps(string? requires = null) =>
    [
        new(null, "Recueil", OpportunityStage.Qualification, 20),
        new(null, "Devis", OpportunityStage.Proposal, 50, RequiredFieldKeys: requires is null ? null : [requires]),
    ];

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private async Task SeedTemplateAsync()
    {
        var template = ProjectTemplate.Create(_tenant, "Parcours", null, null,
        [
            new(null, "Recueil", OpportunityStage.Qualification, 20),
            new(null, "Devis", OpportunityStage.Proposal, 50, RequiredFieldKeys: ["nombreDePersonnes", "devisSigne"]),
        ], DateTimeOffset.UtcNow, fields:
        [
            new(null, "Nombre de personnes", ProjectFieldType.Integer),
            new(null, "Devis signé", ProjectFieldType.Attachment),
        ]).Value!;

        await using var db = _factory.CreateContext();
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        _steps = template.Steps.Select(s => s.Id).ToList();
    }

    private async Task<Guid> SeedProjectAsync()
    {
        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateFromLead(_tenant, Guid.NewGuid(), "Obsèques", "", null, TimeProvider.System);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private ProjectTemplateResolver Resolver(LeadsDbContext db) => new(db, _admin, new FixedTenantContext(_tenant));

    private async Task<Result> MoveAsync(Guid project, Guid step)
    {
        await using var db = _factory.CreateContext();
        var gate = new ProjectStepGate(db, _admin, Substitute.For<IKycModule>(), new FixedTenantContext(_tenant), _user,
            Substitute.For<IEventPublisher>());
        return await new MoveOpportunityToStepHandler(db, Resolver(db), new ProjectStepTransition(TimeProvider.System, gate))
            .Handle(new MoveOpportunityToStepCommand(project, step), CancellationToken.None);
    }

    private async Task<Result> SetAsync(Guid project, Dictionary<string, JsonElement> values)
    {
        await using var db = _factory.CreateContext();
        return await new SetProjectFieldsHandler(db, Resolver(db), TimeProvider.System)
            .Handle(new SetProjectFieldsCommand(project, values), CancellationToken.None);
    }

    private async Task<Result<Guid>> CreateAsync(List<ProjectStepInput> steps, ProductCategory? category = ProductCategory.Loan)
    {
        await using var db = _factory.CreateContext();
        return await new CreateProjectTemplateHandler(db, _admin, new FixedTenantContext(_tenant), TimeProvider.System)
            .Handle(new CreateProjectTemplateCommand("Parcours", category, null, steps), CancellationToken.None);
    }

    /// <summary>A provider with nothing registered: the store is absent, as without a key.</summary>
    private sealed class ServiceCollectionProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
