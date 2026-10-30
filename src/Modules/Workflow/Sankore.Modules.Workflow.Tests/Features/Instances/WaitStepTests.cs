namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Triggers;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Workflow;
using Xunit;

/// <summary>
/// An « Attente » step waits for a delay or an internal event, then the circuit RESUMES. Run as a
/// validation, it sat in nobody's queue and its delay, read as an SLA, ended the instance.
/// </summary>
public sealed class WaitStepTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public WaitStepTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private WorkflowTemplate Template(int? delayHours, string? eventCode, string entityType = "Lead")
    {
        var t = WorkflowTemplate.Create(_tenantId, entityType, "Circuit", Guid.NewGuid());
        t.AddStep(1, "Premier contact", approverRoleCode: "agent");
        t.AddStep(2, "Laisser réfléchir", timeoutHours: delayHours, kind: StepKind.Wait,
            configJson: eventCode is null ? null : $$"""{"eventCode":"{{eventCode}}"}""");
        t.AddStep(3, "Relance", approverRoleCode: "agent");
        t.Activate();
        return t;
    }

    [Fact]
    public void Reaching_a_wait_opens_it_with_its_delay_and_resuming_moves_on()
    {
        var template = Template(48, null);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());
        instance.Approve(Guid.NewGuid(), null, template.Transitions, conditionEvaluator: new ConditionEvaluator());

        var wait = instance.Steps.Single(s => s.Order == 2);
        wait.Status.Should().Be(StepStatus.AwaitingApproval);
        wait.DueAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(48), TimeSpan.FromMinutes(1));

        instance.ResumeWait("Délai d'attente écoulé : le parcours reprend.", template.Transitions,
            conditionEvaluator: new ConditionEvaluator()).Should().BeTrue();

        wait.Status.Should().Be(StepStatus.Executed);
        instance.Status.Should().Be(WorkflowStatus.InProgress);
        instance.CurrentStepOrder.Should().Be(3);
        instance.PendingAuditEntries.Should().Contain(a =>
            a.EventCode == EventCodes.WaitResumed && a.Comment == "Délai d'attente écoulé : le parcours reprend.");
    }

    [Fact]
    public void An_instance_not_on_a_wait_is_left_alone()
    {
        var template = Template(48, null);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());

        instance.ResumeWait("x", template.Transitions, conditionEvaluator: new ConditionEvaluator()).Should().BeFalse();
        instance.CurrentStepOrder.Should().Be(1);
    }

    [Fact]
    public async Task The_awaited_event_on_the_same_record_resumes_the_circuit_others_do_not()
    {
        var template = Template(72, "lead.qualified");
        var leadId = Guid.NewGuid();
        var instanceId = await SeedWaitingAsync(template, leadId);

        await Consume(new WorkflowTriggerSignal(_tenantId, "Lead", leadId, "lead.assigned"));
        await Consume(new WorkflowTriggerSignal(_tenantId, "Lead", Guid.NewGuid(), "lead.qualified"));
        (await CurrentOrderAsync(instanceId)).Should().Be(2, "neither another event nor another lead ends the wait");

        await Consume(new WorkflowTriggerSignal(_tenantId, "Lead", leadId, "lead.qualified"));

        (await CurrentOrderAsync(instanceId)).Should().Be(3);
        await using var db = _factory.CreateContext();
        db.WorkflowAuditEntries.Should().Contain(a =>
            a.InstanceId == instanceId && a.EventCode == EventCodes.WaitResumed && a.Comment == "Événement reçu : « Lead qualifié ».");
    }

    [Fact]
    public async Task The_runner_resumes_an_elapsed_wait_with_its_reason()
    {
        var template = Template(1, "lead.qualified");
        var instanceId = await SeedWaitingAsync(template, Guid.NewGuid());

        await using var scope = Services().CreateAsyncScope();
        var moved = await scope.ServiceProvider.GetRequiredService<InstanceRunner>()
            .ResumeWaitAsync(instanceId, WaitStepConfig.ElapsedReason("""{"eventCode":"lead.qualified"}"""), default);

        moved.Should().BeTrue();
        (await CurrentOrderAsync(instanceId)).Should().Be(3);
        await using var db = _factory.CreateContext();
        db.WorkflowAuditEntries.Should().Contain(a =>
            a.EventCode == EventCodes.WaitResumed && a.Comment == "Délai écoulé sans l'événement « Lead qualifié » : le parcours reprend.");
    }

    [Theory]
    [InlineData(null, null, "Lead", "n'attend ni délai ni événement")]
    [InlineData(null, "lead.inconnu", "Lead", "événement inconnu")]
    [InlineData(24, "client.kyc_validated", "Lead", "ne porte pas sur ce type de dossier")]
    [InlineData(null, "lead.qualified", "Lead", null)]
    [InlineData(24, null, "Opportunity", null)]
    public async Task A_wait_that_could_never_end_is_not_activated(int? delay, string? eventCode, string entityType, string? refusal)
    {
        var template = WorkflowTemplate.Create(_tenantId, entityType, "Circuit", Guid.NewGuid());
        template.AddStep(1, "Premier contact", approverRoleCode: "agent");
        template.AddStep(2, "Laisser réfléchir", timeoutHours: delay, kind: StepKind.Wait,
            configJson: eventCode is null ? null : $$"""{"eventCode":"{{eventCode}}"}""");
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            await seed.SaveChangesAsync();
        }

        var result = await new ActivateTemplateHandler(_factory.CreateContext())
            .Handle(new ActivateTemplateCommand(template.Id), default);

        if (refusal is null) result.IsSuccess.Should().BeTrue(result.Error);
        else result.Error.Should().Contain("« Laisser réfléchir »").And.Contain(refusal);
    }

    private async Task<Guid> SeedWaitingAsync(WorkflowTemplate template, Guid entityId)
    {
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            seed.WorkflowTransitions.AddRange(template.Transitions);
            await seed.SaveChangesAsync();
        }
        var instance = WorkflowInstance.Start(template, entityId, Guid.NewGuid());
        instance.Approve(Guid.NewGuid(), null, template.Transitions, conditionEvaluator: new ConditionEvaluator());
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowInstances.Add(instance);
            seed.WorkflowInstanceSteps.AddRange(instance.Steps);
            await seed.SaveChangesAsync();
        }
        return instance.Id;
    }

    private async Task<int> CurrentOrderAsync(Guid instanceId)
    {
        await using var db = _factory.CreateContext();
        return db.WorkflowInstances.Single(i => i.Id == instanceId).CurrentStepOrder;
    }

    private async Task Consume(WorkflowTriggerSignal signal)
    {
        var context = Substitute.For<ConsumeContext<WorkflowTriggerSignal>>();
        context.Message.Returns(signal);
        await new WorkflowTriggerConsumer(Services().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorkflowTriggerConsumer>.Instance).Consume(context);
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton(Substitute.For<IRuleEvaluator>());
        services.AddSingleton<IConditionEvaluator>(new ConditionEvaluator());
        services.AddSingleton(Substitute.For<IActionExecutorDispatcher>());
        services.AddSingleton(Substitute.For<IBus>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<InstanceRunner>();
        return services.BuildServiceProvider();
    }
}
