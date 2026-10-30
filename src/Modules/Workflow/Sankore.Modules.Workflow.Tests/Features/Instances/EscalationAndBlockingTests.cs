namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Instances.ApproveStep;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Jobs;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// An overdue step is escalated as its « Escalade » says, never ends the instance (product owner,
/// 2026-10-29); a « bloquante » step not yet passed is named to whoever would convert or win.
/// </summary>
public sealed class EscalationAndBlockingTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _leadId = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();

    public EscalationAndBlockingTests()
    {
        _factory = new(_tenantId);
        _leads.GetNotificationFactsAsync(_tenantId, "Lead", _leadId, Arg.Any<CancellationToken>())
            .Returns(new LeadNotificationFacts(_agency, _agent, "Awa", "Ndiaye", null, $"/leads/{_leadId}"));
        _administration.GetUserSupervisorsAsync(_tenantId, _agent, Arg.Any<CancellationToken>())
            .Returns(new UserSupervisors(_manager, null));
        _administration.ListActiveUsersInRolesAsync(_tenantId, Arg.Any<IReadOnlyCollection<string>>(), _agency, Arg.Any<CancellationToken>())
            .Returns([_agent, _colleague]);
        _administration.GetUserNamesAsync(default, default!, default).ReturnsForAnyArgs(
            ci => ((IReadOnlyCollection<Guid>)ci[1]).ToDictionary(id => id, id => id == _manager ? "Moussa Chef" : "Fatou Collègue") as IReadOnlyDictionary<Guid, string>);
        _notifications.NotifyInAppAsync(default!, default).ReturnsForAnyArgs(Result.Ok(Guid.NewGuid()));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Supervisor_then_reassign_hands_the_step_up_then_across_and_the_instance_lives_on()
    {
        var instanceId = await SeedAsync("""{"escalation":"supervisor_then_reassign"}""");

        await Escalator().EscalateAsync(instanceId, default);
        var step = await StepAsync(instanceId);
        step.AssignedToUserId.Should().Be(_manager, "the agent's line manager comes first");
        step.DueAt.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(23));

        await Escalator().EscalateAsync(instanceId, default);
        (await StepAsync(instanceId)).AssignedToUserId.Should().Be(_colleague, "then another holder of the role");

        await using var db = _factory.CreateContext();
        db.WorkflowInstances.Single(i => i.Id == instanceId).Status.Should().Be(WorkflowStatus.InProgress);
        db.WorkflowAuditEntries.Where(a => a.EventCode == EventCodes.Escalated).Select(a => a.Comment).Should().Equal(
            "Délai dépassé : étape confiée à Moussa Chef (supérieur hiérarchique).",
            "Délai dépassé : étape réassignée à Fatou Collègue.");
        await _notifications.Received(1).NotifyInAppAsync(Arg.Is<InAppNotificationRequest>(r => r.UserId == _manager), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_escalation_the_step_stays_and_its_role_holders_are_reminded()
    {
        var instanceId = await SeedAsync(null);

        await Escalator().EscalateAsync(instanceId, default);

        (await StepAsync(instanceId)).AssignedToUserId.Should().BeNull();
        await using var db = _factory.CreateContext();
        db.WorkflowAuditEntries.Should().ContainSingle(a => a.EventCode == EventCodes.SlaReminder
            && a.Comment == "Délai dépassé : responsables relancés.");
        await _notifications.Received(2).NotifyInAppAsync(Arg.Any<InAppNotificationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_supervisor_it_was_handed_to_may_approve_without_the_role()
    {
        var instanceId = await SeedAsync("""{"escalation":"supervisor"}""");
        await Escalator().EscalateAsync(instanceId, default);

        var manager = Substitute.For<ICurrentUser>();
        manager.TenantId.Returns(_tenantId);
        manager.Id.Returns(_manager);
        manager.Roles.Returns(["BranchManager"]);
        var result = await new ApproveStepHandler(_factory.CreateContext(), manager, Substitute.For<IBus>(),
                Substitute.For<IRuleEvaluator>(), new ConditionEvaluator(), Substitute.For<IActionExecutorDispatcher>())
            .Handle(new ApproveStepCommand(instanceId, null), default);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task A_blocking_step_not_yet_passed_is_named_until_it_is()
    {
        var template = WorkflowTemplate.Create(_tenantId, "Lead", "Dossier crédit", Guid.NewGuid());
        template.AddStep(1, "Visite terrain", approverRoleCode: "agent", isBlocking: true);
        template.AddStep(2, "Comité", approverRoleCode: "agent");
        template.Activate();
        await SeedTemplateAsync(template);
        var instance = WorkflowInstance.Start(template, _leadId, Guid.NewGuid());
        await using (var db = _factory.CreateContext())
        {
            db.WorkflowInstances.Add(instance);
            db.WorkflowInstanceSteps.AddRange(instance.Steps);
            await db.SaveChangesAsync();
        }
        var facade = new Sankore.Modules.Workflow.WorkflowModuleFacade(_factory.CreateContext());

        (await facade.GetBlockingStepsAsync(_tenantId, "Lead", _leadId)).Should().Equal("Visite terrain (Dossier crédit)");
        (await facade.GetBlockingStepsAsync(_tenantId, "Lead", Guid.NewGuid())).Should().BeEmpty();

        await using (var db = _factory.CreateContext())
        {
            var tracked = db.WorkflowInstances.AsTracking().Single(i => i.Id == instance.Id);
            await db.Entry(tracked).Collection(i => i.Steps).LoadAsync();
            tracked.Approve(Guid.NewGuid(), null, template.Transitions, conditionEvaluator: new ConditionEvaluator());
            await db.SaveChangesAsync();
        }
        (await new Sankore.Modules.Workflow.WorkflowModuleFacade(_factory.CreateContext())
            .GetBlockingStepsAsync(_tenantId, "Lead", _leadId)).Should().BeEmpty("the blocking step is passed");
    }

    private async Task<Guid> SeedAsync(string? config)
    {
        var template = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        template.AddStep(1, "Validation", approverRoleCode: "agent", timeoutHours: 24, configJson: config);
        template.AddStep(2, "Suite", approverRoleCode: "agent");
        template.Activate();
        await SeedTemplateAsync(template);
        var instance = WorkflowInstance.Start(template, _leadId, Guid.NewGuid());
        await using var db = _factory.CreateContext();
        db.WorkflowInstances.Add(instance);
        db.WorkflowInstanceSteps.AddRange(instance.Steps);
        await db.SaveChangesAsync();
        return instance.Id;
    }

    private async Task SeedTemplateAsync(WorkflowTemplate template)
    {
        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(template);
        seed.WorkflowStepDefinitions.AddRange(template.Steps);
        seed.WorkflowTransitions.AddRange(template.Transitions);
        await seed.SaveChangesAsync();
    }

    private async Task<WorkflowInstanceStep> StepAsync(Guid instanceId)
    {
        await using var db = _factory.CreateContext();
        return db.WorkflowInstanceSteps.Single(s => s.InstanceId == instanceId && s.Order == 1);
    }

    private StepEscalator Escalator()
        => new(_factory.CreateContext(),
            new RecordFactsReader(_leads, Substitute.For<ICustomersModule>(), Substitute.For<IKycModule>()),
            _administration, _notifications, TimeProvider.System, NullLogger<StepEscalator>.Instance);
}
