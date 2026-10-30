namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Gates;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Workflow;
using Xunit;

/// <summary>
/// An « Action Integration » step WAITS for the back office to know the record — the client created
/// at the core banking, the project's contract issued by the insurer — and sends nothing itself
/// (product owner, 2026-10-29): the Integration module already sends both. Before, it was an approval
/// with no role, in nobody's queue.
/// </summary>
public sealed class IntegrationStepTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _recordId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _holdingId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly IWorkflowRecordGate _gate = Substitute.For<IWorkflowRecordGate>();
    private readonly IIntegrationModule _integration = Substitute.For<IIntegrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public IntegrationStepTests()
    {
        _factory = new(_tenantId);
        _gate.EntityType.Returns("Opportunity");
        _gate.ClientIdAsync(_tenantId, _recordId, Arg.Any<CancellationToken>()).Returns(_clientId);
        _customers.GetProjectHoldingIdAsync(_tenantId, _recordId, Arg.Any<CancellationToken>()).Returns(_holdingId);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_core_banking_step_waits_for_the_client_then_passes_when_the_cbs_answers()
    {
        var instanceId = await SeedAsync("""{"target":"CoreBanking"}""");
        var stepId = await StepIdAsync(instanceId);
        _integration.GetSyncStateAsync(_tenantId, IntegrationSyncTarget.CoreBanking, _clientId, Arg.Any<CancellationToken>())
            .Returns(new IntegrationSyncState(false, null), new IntegrationSyncState(true, null));

        await Runner().EvaluateAsync(_tenantId, stepId, default);
        (await CurrentOrderAsync(instanceId)).Should().Be(1);

        await Runner().ReevaluateTenantAsync(_tenantId, default);

        (await CurrentOrderAsync(instanceId)).Should().Be(2);
        await using var db = _factory.CreateContext();
        db.WorkflowAuditEntries.Should().Contain(a => a.EventCode == EventCodes.IntegrationPending
            && a.Comment == "En attente de la création du client au core banking.");
        db.WorkflowAuditEntries.Should().Contain(a => a.EventCode == EventCodes.IntegrationSynced
            && a.Comment == "Client présent dans le core banking.");
    }

    [Fact]
    public async Task A_refusal_of_the_insurer_leaves_the_step_open_with_its_reason()
    {
        var instanceId = await SeedAsync("""{"target":"Insurance"}""");
        _integration.GetSyncStateAsync(_tenantId, IntegrationSyncTarget.Insurance, _holdingId, Arg.Any<CancellationToken>())
            .Returns(new IntegrationSyncState(false, "Âge hors limites"));

        await Runner().EvaluateAsync(_tenantId, await StepIdAsync(instanceId), default);

        (await CurrentOrderAsync(instanceId)).Should().Be(1);
        await using var db = _factory.CreateContext();
        db.WorkflowAuditEntries.Should().Contain(a =>
            a.Comment == "Refus de l'assureur : Âge hors limites. L'étape attend une décision manuelle.");
        (await new RecordStepGates([_gate], Substitute.For<IKycModule>(), _integration, _customers)
                .ApprovalRefusalAsync(_tenantId, "Opportunity", _recordId, StepKind.IntegrationAction, """{"target":"Insurance"}""", default))
            .Should().Contain("Refus de l'assureur");
    }

    [Theory]
    [InlineData("Lead", """{"target":"Insurance"}""", "seuls les parcours Projet")]
    [InlineData("Opportunity", """{"target":"Insurance"}""", null)]
    [InlineData("Lead", """{"target":"CoreBanking"}""", null)]
    public async Task An_insurance_wait_needs_a_project(string entityType, string config, string? refusal)
    {
        var template = WorkflowTemplate.Create(_tenantId, entityType, "Circuit", Guid.NewGuid());
        template.AddStep(1, "Envoi", kind: StepKind.IntegrationAction, configJson: config);
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            await seed.SaveChangesAsync();
        }
        var gate = Substitute.For<IWorkflowRecordGate>();
        gate.EntityType.Returns(entityType);

        var result = await new ActivateTemplateHandler(_factory.CreateContext(), [], [gate])
            .Handle(new ActivateTemplateCommand(template.Id), default);

        if (refusal is null) result.IsSuccess.Should().BeTrue(result.Error);
        else result.Error.Should().Contain(refusal);
    }

    private async Task<Guid> SeedAsync(string config)
    {
        var template = WorkflowTemplate.Create(_tenantId, "Opportunity", "Circuit", Guid.NewGuid());
        template.AddStep(1, "Envoi", kind: StepKind.IntegrationAction, configJson: config);
        template.AddStep(2, "Suite", approverRoleCode: "agent");
        template.Activate();
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            seed.WorkflowTransitions.AddRange(template.Transitions);
            await seed.SaveChangesAsync();
        }
        var instance = WorkflowInstance.Start(template, _recordId, Guid.NewGuid());
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowInstances.Add(instance);
            seed.WorkflowInstanceSteps.AddRange(instance.Steps);
            await seed.SaveChangesAsync();
        }
        await using (var check = _factory.CreateContext())
            check.OutboxMessages.Should().Contain(m => m.EventType == typeof(IntegrationStepReached).AssemblyQualifiedName);
        return instance.Id;
    }

    private async Task<Guid> StepIdAsync(Guid instanceId)
    {
        await using var db = _factory.CreateContext();
        return db.WorkflowInstanceSteps.Single(s => s.InstanceId == instanceId && s.Order == 1).Id;
    }

    private async Task<int> CurrentOrderAsync(Guid instanceId)
    {
        await using var db = _factory.CreateContext();
        return db.WorkflowInstances.Single(i => i.Id == instanceId).CurrentStepOrder;
    }

    private IntegrationStepRunner Runner()
    {
        var db = _factory.CreateContext();
        var runner = new InstanceRunner(db, Substitute.For<IRuleEvaluator>(), new ConditionEvaluator(),
            Substitute.For<IActionExecutorDispatcher>(), Substitute.For<IBus>(), NullLogger<InstanceRunner>.Instance);
        return new IntegrationStepRunner(db, new RecordStepGates([_gate], Substitute.For<IKycModule>(), _integration, _customers),
            runner, NullLogger<IntegrationStepRunner>.Instance);
    }
}
