namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using System.Text.Json;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Outbox;
using Xunit;

/// <summary>
/// A « Tâche manuelle » step asks for a CRM task when it opens, moves on when that task is done,
/// and withdraws it when the step closes some other way. Before, it was an approval and nobody got
/// a task.
/// </summary>
public sealed class ManualTaskStepTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public ManualTaskStepTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private WorkflowTemplate Template()
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        t.AddStep(1, "Appeler le client", approverRoleCode: "agent", timeoutHours: 8, kind: StepKind.ManualTask);
        t.AddStep(2, "Validation", approverRoleCode: "branchmanager");
        t.Activate();
        return t;
    }

    [Fact]
    public async Task Opening_the_step_asks_for_a_task_and_approving_it_by_hand_withdraws_it()
    {
        var (template, instanceId, stepId) = await SeedAsync();

        var requested = Outbox<WorkflowTaskRequested>().Should().ContainSingle().Subject;
        requested.InstanceStepId.Should().Be(stepId);
        requested.StepName.Should().Be("Appeler le client");
        requested.EntityType.Should().Be("Lead");
        requested.RoleCode.Should().Be("agent");
        requested.DueInHours.Should().Be(8);

        await using (var db = _factory.CreateContext())
        {
            var instance = db.WorkflowInstances.AsTracking().Single(i => i.Id == instanceId);
            await db.Entry(instance).Collection(i => i.Steps).LoadAsync();
            instance.Approve(Guid.NewGuid(), "fait sans la tâche", template.Transitions, conditionEvaluator: new ConditionEvaluator());
            db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);
            await db.SaveChangesAsync();
        }

        Outbox<WorkflowTaskWithdrawn>().Should().ContainSingle().Which.InstanceStepId.Should().Be(stepId);
    }

    [Fact]
    public async Task Cancelling_the_instance_withdraws_the_open_task()
    {
        var (_, instanceId, stepId) = await SeedAsync();

        await using (var db = _factory.CreateContext())
        {
            var instance = db.WorkflowInstances.AsTracking().Single(i => i.Id == instanceId);
            await db.Entry(instance).Collection(i => i.Steps).LoadAsync();
            instance.Cancel();
            await db.SaveChangesAsync();
        }

        Outbox<WorkflowTaskWithdrawn>().Should().ContainSingle().Which.InstanceStepId.Should().Be(stepId);
    }

    [Fact]
    public async Task The_task_done_moves_the_circuit_on_in_the_name_of_who_did_it()
    {
        var (_, instanceId, stepId) = await SeedAsync();
        var agent = Guid.NewGuid();

        await Consume(new WorkflowStepTaskClosed(_tenantId, stepId, true, agent, "Appeler le client"));

        await using var db = _factory.CreateContext();
        var steps = db.WorkflowInstanceSteps.Where(s => s.InstanceId == instanceId).OrderBy(s => s.Order).ToList();
        steps[0].Status.Should().Be(StepStatus.Approved);
        steps[0].ActedByUserId.Should().Be(agent);
        steps[1].Status.Should().Be(StepStatus.AwaitingApproval);
        db.WorkflowAuditEntries.Should().Contain(a => a.EventCode == EventCodes.Approve && a.Comment == "Tâche « Appeler le client » terminée.");

        // Done again (redelivery): the step is no longer the open one, nothing moves.
        await Consume(new WorkflowStepTaskClosed(_tenantId, stepId, true, agent, "Appeler le client"));
        await using var again = _factory.CreateContext();
        again.WorkflowInstances.Single(i => i.Id == instanceId).CurrentStepOrder.Should().Be(2);
    }

    [Fact]
    public async Task A_cancelled_task_leaves_the_step_open_and_says_so()
    {
        var (_, instanceId, stepId) = await SeedAsync();

        await Consume(new WorkflowStepTaskClosed(_tenantId, stepId, false, null, "Appeler le client"));

        await using var db = _factory.CreateContext();
        db.WorkflowInstanceSteps.Single(s => s.Id == stepId).Status.Should().Be(StepStatus.AwaitingApproval);
        db.WorkflowAuditEntries.Should().ContainSingle(a => a.InstanceId == instanceId && a.EventCode == EventCodes.TaskCancelled);
    }

    private async Task<(WorkflowTemplate, Guid InstanceId, Guid StepId)> SeedAsync()
    {
        var template = Template();
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            seed.WorkflowTransitions.AddRange(template.Transitions);
            await seed.SaveChangesAsync();
        }
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowInstances.Add(instance);
            seed.WorkflowInstanceSteps.AddRange(instance.Steps);
            await seed.SaveChangesAsync();
        }
        return (template, instance.Id, instance.Steps.Single(s => s.Order == 1).Id);
    }

    private List<T> Outbox<T>()
    {
        using var db = _factory.CreateContext();
        return db.OutboxMessages
            .Where(m => m.EventType == typeof(T).AssemblyQualifiedName)
            .AsEnumerable()
            .Select(m => JsonSerializer.Deserialize<T>(m.PayloadJson, OutboxJson.Options)!)
            .ToList();
    }

    private async Task Consume(WorkflowStepTaskClosed closed)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton(Substitute.For<IRuleEvaluator>());
        services.AddSingleton<IConditionEvaluator>(new ConditionEvaluator());
        services.AddSingleton(Substitute.For<IActionExecutorDispatcher>());
        services.AddSingleton(Substitute.For<IBus>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<InstanceRunner>();
        var provider = services.BuildServiceProvider();

        var context = Substitute.For<ConsumeContext<WorkflowStepTaskClosed>>();
        context.Message.Returns(closed);
        await new WorkflowStepTaskClosedConsumer(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorkflowStepTaskClosedConsumer>.Instance).Consume(context);
    }
}
