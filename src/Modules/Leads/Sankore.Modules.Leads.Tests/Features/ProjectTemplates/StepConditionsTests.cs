namespace Sankore.Modules.Leads.Tests.Features.ProjectTemplates;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.Opportunities.StepConditions;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Les conditions de passage d'une étape et le circuit de validation lié (propriétaire,
/// 2026-10-24) : montant et date pour ATTEINDRE l'étape, points cochés pour la QUITTER, circuit
/// lancé à l'arrivée et approuvé avant de repartir — un refus bloque jusqu'à la relance.
/// </summary>
public sealed class StepConditionsTests : IDisposable
{
    private const string Circuit = "comite-credit";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IWorkflowModule _workflow = Substitute.For<IWorkflowModule>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Dictionary<Guid, string> _instances = new();

    private List<Guid> _steps = [];
    private Guid _visitItem;

    public StepConditionsTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _user.Id.Returns(Guid.NewGuid());
        _user.TenantId.Returns(_tenant);

        // Every start opens a new instance « InProgress »; the tests decide it afterwards.
        _workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var id = Guid.NewGuid();
                _instances[id] = "InProgress";
                return Result.Ok(id);
            });
        _workflow.GetInstanceStatusAsync(Arg.Any<Guid>(), _tenant, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<Guid>(0);
                return Result.Ok(new WorkflowInstanceStatus(id, _instances[id], 1, 1, _instances[id] == "Completed"));
            });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void A_step_s_checklist_is_refused_with_two_identical_points()
    {
        var created = ProjectTemplate.Create(_tenant, "Parcours", null, null,
            [new(null, "Montage", OpportunityStage.NeedsAnalysis, 40,
                Checklist: [new(null, "Visite faite"), new(null, "visite faite ")])],
            DateTimeOffset.UtcNow);

        created.Error.Should().Be(ProjectTemplateErrors.StepChecklistItemDuplicate);
    }

    [Fact]
    public async Task Reaching_a_step_that_needs_the_amount_is_refused_without_one()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync(amount: null);

        (await MoveAsync(project, _steps[1])).Error.Should().Be(ProjectTemplateErrors.AmountRequired);
    }

    [Fact]
    public async Task Leaving_a_step_needs_its_points_ticked()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync(amount: 5_000_000m);
        (await MoveAsync(project, _steps[1])).IsSuccess.Should().BeTrue();

        (await MoveAsync(project, _steps[2])).Error.Should().Be(ProjectTemplateErrors.ChecklistIncomplete);

        (await TickAsync(project, _visitItem)).IsSuccess.Should().BeTrue();
        (await MoveAsync(project, _steps[2])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_point_of_another_template_cannot_be_ticked()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync(amount: 1m);

        (await TickAsync(project, Guid.NewGuid())).Error.Should().Be(ProjectTemplateErrors.ChecklistItemUnknown);
    }

    [Fact]
    public async Task Reaching_a_linked_step_starts_its_circuit_and_leaving_it_needs_the_approval()
    {
        var project = await AtCommitteeAsync();

        var run = (await LoadAsync(project)).WorkflowRuns.Should().ContainSingle().Subject;
        run.Circuit.Should().Be(Circuit);
        await _workflow.Received(1).StartWorkflowAsync(
            Arg.Is<WorkflowStartRequest>(r => r.EntityType == "Opportunity" && r.EntityId == project && r.Circuit == Circuit),
            Arg.Any<CancellationToken>());

        (await MoveAsync(project, _steps[3])).Error.Should().Be(ProjectTemplateErrors.WorkflowPending);

        _instances[run.InstanceId] = "Completed";
        (await MoveAsync(project, _steps[3])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task The_circuit_tells_the_approver_what_they_validate()
    {
        await AtCommitteeAsync();

        await _workflow.Received(1).StartWorkflowAsync(
            Arg.Is<WorkflowStartRequest>(r => r.Subject != null
                && r.Subject.Label == "Crédit chantier"
                && r.Subject.Detail!.Contains("XOF") && r.Subject.Detail.Contains("« Comité »")
                && r.Subject.Link!.StartsWith("/leads/projets/")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_project_shows_who_validated_each_run_of_a_step_s_circuit()
    {
        var project = await AtCommitteeAsync();
        var run = (await LoadAsync(project)).WorkflowRuns.Single();
        var approver = Guid.NewGuid();
        _workflow.GetInstanceDecisionsAsync(_tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<WorkflowInstanceDecisions>
            {
                new(run.InstanceId, "Comité crédit PME", "Rejected", run.StartedAt, run.StartedAt.AddHours(2),
                [
                    new(1, "Validation", "Administrator", "Rejected", approver, "Garanties insuffisantes", null, run.StartedAt.AddHours(2)),
                ]),
            });
        _admin.GetUserNamesAsync(_tenant, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [approver] = "Awa Sangaré" });

        await using var db = _factory.CreateContext();
        var validations = (await new GetProjectValidationsHandler(db, Resolver(db), _workflow, _admin, new FixedTenantContext(_tenant))
            .Handle(new GetProjectValidationsQuery(project), CancellationToken.None)).Value!;

        var v = validations.Should().ContainSingle().Subject;
        v.StepLabel.Should().Be("Comité");
        v.WorkflowName.Should().Be("Comité crédit PME");
        v.Status.Should().Be("Rejected");
        v.Decisions.Should().ContainSingle().Which.Should().Be(new ProjectValidationDecisionDto(
            1, "Validation", "Compte Administrateur", "Rejected", "Awa Sangaré", "Garanties insuffisantes", null, run.StartedAt.AddHours(2)));
    }

    [Fact]
    public async Task A_refused_circuit_blocks_until_it_is_run_again()
    {
        var project = await AtCommitteeAsync();
        var first = (await LoadAsync(project)).WorkflowRuns.Single();
        _instances[first.InstanceId] = "Rejected";

        (await MoveAsync(project, _steps[3])).Error.Should().Be(ProjectTemplateErrors.WorkflowRejected);
        (await CloseAsync(project)).Error.Should().Be(ProjectTemplateErrors.WorkflowRejected, "winning does not jump the committee");

        (await RestartAsync(project)).IsSuccess.Should().BeTrue();
        var runs = (await LoadAsync(project)).WorkflowRuns;
        runs.Should().HaveCount(2, "the refused run stays readable");
        (await RestartAsync(project)).Error.Should().Be(ProjectTemplateErrors.WorkflowNotRestartable, "the new run is pending");
    }

    [Fact]
    public async Task Winning_from_an_early_step_still_owes_the_committee()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync(amount: 5_000_000m);
        (await MoveAsync(project, _steps[1])).IsSuccess.Should().BeTrue();
        await TickAsync(project, _visitItem);

        (await CloseAsync(project)).Error.Should().Be(ProjectTemplateErrors.WorkflowPending);
    }

    [Fact]
    public async Task The_legacy_phase_move_goes_through_the_same_checks()
    {
        // The client's Projets tab moves by phase: it used to skip every condition.
        await SeedTemplateAsync();
        var project = await SeedProjectAsync(amount: null);

        await using var db = _factory.CreateContext();
        var result = await new AdvanceOpportunityStageHandler(db, TimeProvider.System, Resolver(db), Transition(db))
            .Handle(new AdvanceOpportunityStageCommand(project, OpportunityStage.NeedsAnalysis), CancellationToken.None);

        result.Error.Should().Be(ProjectTemplateErrors.AmountRequired);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Contact → Montage (montant, 1 point) → Comité (circuit) → Accord.</summary>
    private async Task SeedTemplateAsync()
    {
        var template = ProjectTemplate.Create(_tenant, "Parcours", null, null,
        [
            new(null, "Contact", OpportunityStage.Prospecting, 10),
            new(null, "Montage", OpportunityStage.NeedsAnalysis, 40, RequiresAmount: true,
                Checklist: [new(null, "Visite du chantier faite")]),
            new(null, "Comité", OpportunityStage.Proposal, 70, WorkflowCircuit: Circuit),
            new(null, "Accord", OpportunityStage.Negotiation, 90),
        ], DateTimeOffset.UtcNow).Value!;

        await using var db = _factory.CreateContext();
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        _steps = template.Steps.Select(s => s.Id).ToList();
        _visitItem = template.Steps[1].Checklist.Single().Id;
    }

    private async Task<Guid> SeedProjectAsync(decimal? amount)
    {
        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateFromLead(_tenant, Guid.NewGuid(), "Crédit chantier", "", null, TimeProvider.System,
            estimatedAmount: amount is { } a ? new Money(a, "XOF") : null);
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Guid> AtCommitteeAsync()
    {
        await SeedTemplateAsync();
        var project = await SeedProjectAsync(amount: 5_000_000m);
        (await MoveAsync(project, _steps[1])).IsSuccess.Should().BeTrue();
        await TickAsync(project, _visitItem);
        (await MoveAsync(project, _steps[2])).IsSuccess.Should().BeTrue();
        return project;
    }

    private ProjectTemplateResolver Resolver(LeadsDbContext db) => new(db, _admin, new FixedTenantContext(_tenant));

    private ProjectWorkflows Workflows() => new(_workflow, new FixedTenantContext(_tenant), _user, TimeProvider.System);

    private ProjectStepGate Gate(LeadsDbContext db) => new(
        db, _admin, Substitute.For<IKycModule>(), new FixedTenantContext(_tenant), _user,
        Substitute.For<IEventPublisher>(), questionnaires: null, workflows: Workflows());

    private ProjectStepTransition Transition(LeadsDbContext db)
        => new(TimeProvider.System, Gate(db), conversion: null, workflows: Workflows());

    private async Task<Result> MoveAsync(Guid project, Guid step)
    {
        await using var db = _factory.CreateContext();
        return await new MoveOpportunityToStepHandler(db, Resolver(db), Transition(db))
            .Handle(new MoveOpportunityToStepCommand(project, step), CancellationToken.None);
    }

    private async Task<Result> TickAsync(Guid project, Guid item)
    {
        await using var db = _factory.CreateContext();
        return await new SetProjectChecklistItemHandler(db, Resolver(db), TimeProvider.System)
            .Handle(new SetProjectChecklistItemCommand(project, item, Done: true), CancellationToken.None);
    }

    private async Task<Result> RestartAsync(Guid project)
    {
        await using var db = _factory.CreateContext();
        return await new RestartProjectWorkflowHandler(db, Resolver(db), Workflows())
            .Handle(new RestartProjectWorkflowCommand(project), CancellationToken.None);
    }

    private async Task<Result> CloseAsync(Guid project)
    {
        await using var db = _factory.CreateContext();
        var result = await new CloseOpportunityHandler(
                db, Substitute.For<ICustomersModule>(), _admin, _user, TimeProvider.System,
                NullLogger<CloseOpportunityHandler>.Instance, Resolver(db), Gate(db))
            .Handle(new CloseOpportunityCommand(project, OpportunityStage.ClosedWon, "signé", null), CancellationToken.None);
        return result.IsSuccess ? Result.Ok() : Result.Fail(result.Error!);
    }

    private async Task<Opportunity> LoadAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Opportunities.SingleAsync(o => o.Id == id);
    }
}
