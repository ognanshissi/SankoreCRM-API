namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Instances.ApproveStep;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Gates;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Workflow;
using Xunit;

/// <summary>
/// « Formulaire », « Formulaire de qualification » and « Vérification KYC » steps wait on the record:
/// they are not approved while it does not meet them, and the KYC check passes by itself once the
/// client's KYC is there. Before, all three were plain approvals.
/// </summary>
public sealed class RecordGateStepTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _leadId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly IWorkflowRecordGate _gate = Substitute.For<IWorkflowRecordGate>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();

    public RecordGateStepTests()
    {
        _factory = new(_tenantId);
        _gate.EntityType.Returns("Lead");
        _gate.ClientIdAsync(_tenantId, _leadId, Arg.Any<CancellationToken>()).Returns(_clientId);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_form_step_is_not_approved_while_its_fields_are_empty()
    {
        var instanceId = await SeedAsync(StepKind.Form, """{"fields":["email","custom.revenu"]}""");
        _gate.MissingFieldsAsync(_tenantId, _leadId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "E-mail", "Revenu mensuel" }, new List<string>());

        (await Approve(instanceId)).Error.Should().Be("Champs à remplir avant d'approuver : E-mail, Revenu mensuel.");
        (await Approve(instanceId)).IsSuccess.Should().BeTrue("once filled, the step is approved as any other");
    }

    [Fact]
    public async Task A_qualification_step_is_not_approved_while_a_questionnaire_is_to_complete()
    {
        var instanceId = await SeedAsync(StepKind.QualificationForm, null);
        _gate.PendingQuestionnairesAsync(_tenantId, _leadId, Arg.Any<CancellationToken>()).Returns(2, 0);

        (await Approve(instanceId)).Error.Should().Be("2 questionnaires à compléter avant d'approuver.");
        (await Approve(instanceId)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_kyc_step_passes_by_itself_once_the_client_reaches_the_level()
    {
        var instanceId = await SeedAsync(StepKind.KycCheck, """{"kycLevel":"Full"}""");
        var stepId = await StepIdAsync(instanceId, 1);
        await using (var db = _factory.CreateContext())
            db.OutboxMessages.Should().ContainSingle(m => m.EventType == typeof(KycCheckStepReached).AssemblyQualifiedName);

        // Validated, but only simplified: the step waits, and says so.
        _kyc.GetStatusAsync(_tenantId, _clientId, Arg.Any<CancellationToken>()).Returns(KycStatus.Approved);
        _kyc.GetLimitsAsync(_tenantId, _clientId, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Simplified", true, 0, 0, 30, 80));
        await Runner().EvaluateAsync(_tenantId, stepId, default);
        (await CurrentOrderAsync(instanceId)).Should().Be(1);
        (await Approve(instanceId)).Error.Should().Contain("l'étape attend un KYC complet");

        // The KYC is completed: the client's validation re-checks the steps depending on it.
        _kyc.GetLimitsAsync(_tenantId, _clientId, Arg.Any<CancellationToken>())
            .Returns(new KycLimits("Full", false, 0, 0, 30, 80));
        await Runner().ReevaluateForClientAsync(_tenantId, _clientId, default);

        (await CurrentOrderAsync(instanceId)).Should().Be(2);
        await using var verify = _factory.CreateContext();
        verify.WorkflowInstanceSteps.Single(s => s.Id == stepId).Status.Should().Be(StepStatus.Executed);
        verify.WorkflowAuditEntries.Should().Contain(a => a.EventCode == EventCodes.KycPending
            && a.Comment == "KYC du client validé en simplifié : l'étape attend un KYC complet.");
        verify.WorkflowAuditEntries.Should().Contain(a => a.EventCode == EventCodes.KycValidated
            && a.Comment == "KYC du client validé (complet).");
    }

    [Fact]
    public async Task A_lead_not_yet_a_client_waits_for_one()
    {
        var instanceId = await SeedAsync(StepKind.KycCheck, """{"kycLevel":"Simplified"}""");
        _gate.ClientIdAsync(_tenantId, _leadId, Arg.Any<CancellationToken>()).Returns((Guid?)null);

        await Runner().EvaluateAsync(_tenantId, await StepIdAsync(instanceId, 1), default);

        (await CurrentOrderAsync(instanceId)).Should().Be(1);
        await using var db = _factory.CreateContext();
        db.WorkflowAuditEntries.Should().Contain(a => a.Comment == "Pas encore de fiche client : l'étape attend un KYC simplifié validé.");
    }

    [Theory]
    [InlineData(StepKind.Form, null, "Lead", true, "choisissez les champs")]
    [InlineData(StepKind.Form, """{"fields":["email"]}""", "KycFile", false, "pas de formulaire")]
    [InlineData(StepKind.QualificationForm, null, "Account", true, "pas de questionnaire")]
    [InlineData(StepKind.KycCheck, null, "KycFile", false, null)]
    [InlineData(StepKind.Form, """{"fields":["email"]}""", "Lead", true, null)]
    public async Task A_step_the_record_cannot_answer_is_not_activated(StepKind kind, string? config, string entityType, bool withGate, string? refusal)
    {
        var template = WorkflowTemplate.Create(_tenantId, entityType, "Circuit", Guid.NewGuid());
        template.AddStep(1, "Étape", approverRoleCode: "agent", kind: kind, configJson: config);
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            await seed.SaveChangesAsync();
        }
        var gate = Substitute.For<IWorkflowRecordGate>();
        gate.EntityType.Returns(entityType);

        var result = await new ActivateTemplateHandler(_factory.CreateContext(), [], withGate ? [gate] : [])
            .Handle(new ActivateTemplateCommand(template.Id), default);

        if (refusal is null) result.IsSuccess.Should().BeTrue(result.Error);
        else result.Error.Should().Contain(refusal);
    }

    private async Task<Guid> SeedAsync(StepKind kind, string? config)
    {
        var template = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        template.AddStep(1, "Étape vérifiée", approverRoleCode: "agent", kind: kind, configJson: config);
        template.AddStep(2, "Suite", approverRoleCode: "agent");
        template.Activate();
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            seed.WorkflowTransitions.AddRange(template.Transitions);
            await seed.SaveChangesAsync();
        }
        var instance = WorkflowInstance.Start(template, _leadId, Guid.NewGuid());
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowInstances.Add(instance);
            seed.WorkflowInstanceSteps.AddRange(instance.Steps);
            await seed.SaveChangesAsync();
        }
        return instance.Id;
    }

    private async Task<Guid> StepIdAsync(Guid instanceId, int order)
    {
        await using var db = _factory.CreateContext();
        return db.WorkflowInstanceSteps.Single(s => s.InstanceId == instanceId && s.Order == order).Id;
    }

    private async Task<int> CurrentOrderAsync(Guid instanceId)
    {
        await using var db = _factory.CreateContext();
        return db.WorkflowInstances.Single(i => i.Id == instanceId).CurrentStepOrder;
    }

    private async Task<Sankore.Shared.Kernel.Result> Approve(Guid instanceId)
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenantId);
        user.Id.Returns(Guid.NewGuid());
        user.Roles.Returns(["agent"]);
        return await new ApproveStepHandler(_factory.CreateContext(), user, Substitute.For<IBus>(),
                Substitute.For<IRuleEvaluator>(), new ConditionEvaluator(), Substitute.For<IActionExecutorDispatcher>(),
                new RecordStepGates([_gate], _kyc))
            .Handle(new ApproveStepCommand(instanceId, null), default);
    }

    private KycStepRunner Runner()
    {
        var db = _factory.CreateContext();
        var runner = new InstanceRunner(db, Substitute.For<IRuleEvaluator>(), new ConditionEvaluator(),
            Substitute.For<IActionExecutorDispatcher>(), Substitute.For<IBus>(), NullLogger<InstanceRunner>.Instance);
        return new KycStepRunner(db, new RecordStepGates([_gate], _kyc), runner, NullLogger<KycStepRunner>.Instance);
    }
}
