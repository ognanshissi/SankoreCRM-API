namespace Sankore.Modules.Workflow.Tests.Infrastructure;

using FluentAssertions;
using NSubstitute;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Xunit;

/// <summary>
/// « Plusieurs workflows Projet actifs, un par étape liée » (propriétaire, 2026-10-24) : un comité
/// crédit et un comité assurance groupe sont deux CIRCUITS du même type « Opportunity » (le projet). Les autres types
/// gardent leur circuit unique.
/// </summary>
public sealed class ProjectCircuitsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public ProjectCircuitsTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("Opportunity", "Comité crédit PME", "comite-credit-pme")]
    [InlineData("Opportunity", "  Assurance / Groupe  ", "assurance-groupe")]
    [InlineData("KycFile", "Validation KYC", "")]
    public void A_project_workflow_is_a_circuit_named_after_it(string entityType, string name, string expected)
        => WorkflowTemplate.CircuitFor(entityType, name).Should().Be(expected);

    [Fact]
    public async Task Two_project_circuits_can_be_active_and_each_is_started_by_its_key()
    {
        var credit = await SeedAsync("Comité crédit PME");
        var insurance = await SeedAsync("Comité assurance groupe");

        (await ActivateAsync(credit)).IsSuccess.Should().BeTrue();
        (await ActivateAsync(insurance)).IsSuccess.Should().BeTrue("another circuit of the same type");

        var facade = new WorkflowModuleFacade(_factory.CreateContext());
        (await facade.ListActiveCircuitsAsync(_tenantId, WorkflowTemplate.ProjectEntityType))
            .Select(c => c.Circuit).Should().BeEquivalentTo(["comite-credit-pme", "comite-assurance-groupe"]);

        var started = await facade.StartWorkflowAsync(new WorkflowStartRequest(
            WorkflowTemplate.ProjectEntityType, Guid.NewGuid(), Guid.NewGuid(), _tenantId, Circuit: "comite-assurance-groupe"));

        await using var verify = _factory.CreateContext();
        (await verify.WorkflowInstances.IgnoreQueryFilters().SingleAsync(i => i.Id == started.Value))
            .TemplateId.Should().Be(insurance.Id);
    }

    [Fact]
    public async Task A_second_version_of_the_same_circuit_still_waits_for_the_first_to_be_deactivated()
    {
        var v1 = await SeedAsync("Comité crédit PME");
        (await ActivateAsync(v1)).IsSuccess.Should().BeTrue();

        var v2 = v1.CreateDraft(Guid.NewGuid());
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(v2);
            await seed.SaveChangesAsync();
        }

        v2.Circuit.Should().Be(v1.Circuit, "a new version keeps its circuit, so a project step's link survives it");
        (await ActivateAsync(v2)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task An_approver_sees_what_they_validate_and_the_starter_reads_the_decisions()
    {
        // The queue used to show « Validation » and nothing else: of which project, for how much?
        var template = await SeedAsync("Comité crédit PME", approverRole: "Administrator");
        (await ActivateAsync(template)).IsSuccess.Should().BeTrue();

        var facade = new WorkflowModuleFacade(_factory.CreateContext());
        var started = await facade.StartWorkflowAsync(new WorkflowStartRequest(
            WorkflowTemplate.ProjectEntityType, Guid.NewGuid(), Guid.NewGuid(), _tenantId,
            Circuit: "comite-credit-pme",
            Subject: new WorkflowSubject("Crédit chantier Daloa", "Awa Koné · Crédit PME · 5 000 000 XOF", "/leads/projets/x")));

        var approver = Substitute.For<Sankore.Shared.Infrastructure.Auth.ICurrentUser>();
        approver.Id.Returns(Guid.NewGuid());
        approver.Roles.Returns(["Administrator"]);
        var mine = (await new Sankore.Modules.Workflow.Features.Instances.ListMySteps.ListMyStepsHandler(_factory.CreateContext(), approver)
            .Handle(new Sankore.Modules.Workflow.Features.Instances.ListMySteps.ListMyStepsQuery(), default)).Value!;

        mine.Should().ContainSingle().Which.Should().Match<Sankore.Modules.Workflow.Features.Instances.ListMySteps.MyStepDto>(s =>
            s.SubjectLabel == "Crédit chantier Daloa"
            && s.SubjectDetail == "Awa Koné · Crédit PME · 5 000 000 XOF"
            && s.SubjectLink == "/leads/projets/x"
            && s.WorkflowName == "Comité crédit PME");

        var decisions = await new WorkflowModuleFacade(_factory.CreateContext())
            .GetInstanceDecisionsAsync(_tenantId, [started.Value]);
        decisions.Should().ContainSingle().Which.Steps.Should().ContainSingle()
            .Which.Status.Should().Be("AwaitingApproval");
    }

    private async Task<WorkflowTemplate> SeedAsync(string name, string? approverRole = null)
    {
        var template = WorkflowTemplate.Create(
            _tenantId, WorkflowTemplate.ProjectEntityType, name, Guid.Empty,
            circuit: WorkflowTemplate.CircuitFor(WorkflowTemplate.ProjectEntityType, name));
        template.AddStep(1, "Comité", approverRoleCode: approverRole);

        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(template);
        await seed.SaveChangesAsync();
        return template;
    }

    private Task<Sankore.Shared.Kernel.Result> ActivateAsync(WorkflowTemplate template)
        => new ActivateTemplateHandler(_factory.CreateContext())
            .Handle(new ActivateTemplateCommand(template.Id), CancellationToken.None);
}
