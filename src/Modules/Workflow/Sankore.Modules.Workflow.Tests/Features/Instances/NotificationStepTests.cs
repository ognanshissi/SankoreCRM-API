namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using System.Text.Json;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Features.Instances.ApproveStep;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Outbox;
using Xunit;

/// <summary>
/// A « Notification » step used to be run as a validation with no role: the instance stopped there,
/// in nobody's « mes étapes », and nothing was ever sent. It is now franchie on entry and leaves a
/// <see cref="WorkflowNotificationRequested"/> in the outbox.
/// </summary>
public sealed class NotificationStepTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public NotificationStepTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private WorkflowTemplate Template(params StepKind[] kinds)
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        for (var i = 0; i < kinds.Length; i++)
        {
            var notification = kinds[i] == StepKind.Notification;
            t.AddStep(i + 1, notification ? "Prévenir le lead" : $"Validation {i + 1}",
                approverRoleCode: notification ? null : "branchmanager",
                kind: kinds[i],
                configJson: notification ? """{"channel":"Sms"}""" : null);
        }
        t.Activate();
        return t;
    }

    [Fact]
    public void A_first_notification_step_is_franchie_at_start()
    {
        var instance = WorkflowInstance.Start(Template(StepKind.Notification, StepKind.Validation), Guid.NewGuid(), Guid.NewGuid());

        instance.Steps.Single(s => s.Order == 1).Status.Should().Be(StepStatus.Executed);
        instance.Steps.Single(s => s.Order == 2).Status.Should().Be(StepStatus.AwaitingApproval);
        instance.CurrentStepOrder.Should().Be(2);
        instance.DomainEvents.OfType<NotificationStepExecutedEvent>().Should().ContainSingle()
            .Which.ConfigJson.Should().Be("""{"channel":"Sms"}""");
    }

    [Fact]
    public void Approving_into_a_notification_step_franchit_it_through_the_state_machine()
    {
        var template = Template(StepKind.Validation, StepKind.Notification, StepKind.Validation);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());

        instance.Approve(Guid.NewGuid(), null, template.Transitions, conditionEvaluator: new ConditionEvaluator());

        instance.Steps.Single(s => s.Order == 2).Status.Should().Be(StepStatus.Executed);
        instance.Steps.Single(s => s.Order == 3).Status.Should().Be(StepStatus.AwaitingApproval);
        instance.CurrentStepOrder.Should().Be(3);
        instance.PendingAuditEntries.Select(a => a.EventCode).Should().Equal(EventCodes.Approve, EventCodes.Auto);
        instance.DomainEvents.OfType<NotificationStepExecutedEvent>().Should().ContainSingle();
    }

    [Fact]
    public void A_closing_notification_step_completes_the_instance()
    {
        var template = Template(StepKind.Validation, StepKind.Notification, StepKind.Notification);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());

        instance.Approve(Guid.NewGuid(), null, template.Transitions, conditionEvaluator: new ConditionEvaluator());

        instance.Status.Should().Be(WorkflowStatus.Completed);
        instance.DomainEvents.OfType<NotificationStepExecutedEvent>().Should().HaveCount(2);
        instance.DomainEvents.OfType<WorkflowCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task The_request_is_written_to_the_outbox_once_in_the_approval_transaction()
    {
        var template = Template(StepKind.Validation, StepKind.Notification, StepKind.Validation);
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

        var result = await ApproveHandler().Handle(new ApproveStepCommand(instance.Id, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        await using var verify = _factory.CreateContext();
        var row = verify.OutboxMessages.Should().ContainSingle().Subject;
        row.EventType.Should().Be(typeof(WorkflowNotificationRequested).AssemblyQualifiedName);
        var request = JsonSerializer.Deserialize<WorkflowNotificationRequested>(row.PayloadJson, OutboxJson.Options)!;
        var stepId = verify.WorkflowInstanceSteps.Single(s => s.InstanceId == instance.Id && s.Order == 2).Id;
        request.InstanceStepId.Should().Be(stepId);
        request.IdempotencyKey.Should().Be($"workflow.notification:{stepId}");
        request.TenantId.Should().Be(_tenantId);
        request.EntityType.Should().Be("Lead");
        request.EntityId.Should().Be(instance.EntityId);
        request.Channel.Should().Be("Sms");
        request.Recipients.Should().Equal(WorkflowNotificationRecipients.Subject);
    }

    [Fact]
    public void A_second_save_of_the_same_context_announces_nothing_twice()
    {
        var instance = WorkflowInstance.Start(Template(StepKind.Notification, StepKind.Validation), Guid.NewGuid(), Guid.NewGuid());
        using var db = _factory.CreateContext();
        db.WorkflowInstances.Add(instance);
        db.WorkflowInstanceSteps.AddRange(instance.Steps);

        db.SaveChanges();
        db.SaveChanges();

        db.OutboxMessages.Count().Should().Be(1);
    }

    [Fact]
    public void The_configuration_names_recipients_roles_and_text()
    {
        var request = NotificationStepConfig.ToRequest(Executed(
            """{"channel":"Email","recipients":["role","assignedAgent","role"],"roleCodes":["BranchManager"],"subject":" Dossier reçu ","message":"Bonjour {{prenom}}"}"""));

        request.Channel.Should().Be("Email");
        request.Recipients.Should().Equal("role", "assignedAgent");
        request.RoleCodes.Should().Equal("BranchManager");
        request.Subject.Should().Be("Dossier reçu");
        request.Message.Should().Be("Bonjour {{prenom}}");
    }

    [Theory]
    [InlineData(null, "subject")]
    [InlineData("""{"channel":"Email"}""", "subject")]
    [InlineData("""{"channel":"Email","recipients":[]}""", "")]
    [InlineData("not json", "subject")]
    public void A_step_saved_before_recipients_existed_tells_the_person_of_the_record(string? configJson, string expected)
    {
        var request = NotificationStepConfig.ToRequest(Executed(configJson));

        string.Join(",", request.Recipients).Should().Be(expected);
        request.Channel.Should().Be("Email");
    }

    [Fact]
    public async Task An_instance_stuck_on_a_notification_step_is_released_at_start()
    {
        // A notification step opened before the engine ran them: built as a validation, then given
        // the kind the migration copies from its definition.
        var template = Template(StepKind.Validation, StepKind.Validation, StepKind.Validation);
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
            var first = instance.Steps.Single(s => s.Order == 1);
            seed.Entry(first).Property(s => s.Kind).CurrentValue = StepKind.Notification;
            await seed.SaveChangesAsync();
        }

        var bus = Substitute.For<IBus>();
        await CatchUp(bus).RunAsync(CancellationToken.None);

        await using var verify = _factory.CreateContext();
        var steps = verify.WorkflowInstanceSteps.Where(s => s.InstanceId == instance.Id).OrderBy(s => s.Order).ToList();
        steps[0].Status.Should().Be(StepStatus.Executed);
        steps[1].Status.Should().Be(StepStatus.AwaitingApproval);
        verify.WorkflowInstances.Single(i => i.Id == instance.Id).CurrentStepOrder.Should().Be(2);
        verify.OutboxMessages.Should().ContainSingle();
        verify.WorkflowAuditEntries.Should().ContainSingle(a => a.EventCode == EventCodes.Auto);

        // A second start finds nothing left to release.
        await CatchUp(bus).RunAsync(CancellationToken.None);
        await using var again = _factory.CreateContext();
        again.OutboxMessages.Count().Should().Be(1);
    }

    [Theory]
    [InlineData(null, "n'a pas de message")]
    [InlineData("""{"channel":"Email","recipients":[],"message":"Bonjour"}""", "n'a pas de destinataire")]
    [InlineData("""{"recipients":["role"],"message":"Bonjour"}""", "sans en nommer aucun")]
    [InlineData("""{"recipients":["subject","role"],"roleCodes":["BranchManager"],"message":"Bonjour"}""", null)]
    public async Task A_circuit_whose_notification_would_send_nothing_is_not_activated(string? configJson, string? refusal)
    {
        var template = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        template.AddStep(1, "Validation", approverRoleCode: "branchmanager");
        template.AddStep(2, "Prévenir", kind: StepKind.Notification, configJson: configJson);
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowTemplates.Add(template);
            seed.WorkflowStepDefinitions.AddRange(template.Steps);
            await seed.SaveChangesAsync();
        }

        var result = await new Sankore.Modules.Workflow.Features.Templates.ActivateTemplate.ActivateTemplateHandler(_factory.CreateContext())
            .Handle(new Sankore.Modules.Workflow.Features.Templates.ActivateTemplate.ActivateTemplateCommand(template.Id), default);

        if (refusal is null) result.IsSuccess.Should().BeTrue(result.Error);
        else result.Error.Should().Contain("« Prévenir »").And.Contain(refusal);
    }

    [Fact]
    public async Task What_became_of_each_audience_is_written_under_the_step_once()
    {
        var instance = WorkflowInstance.Start(Template(StepKind.Notification, StepKind.Validation), Guid.NewGuid(), Guid.NewGuid());
        await using (var seed = _factory.CreateContext())
        {
            seed.WorkflowInstances.Add(instance);
            seed.WorkflowInstanceSteps.AddRange(instance.Steps);
            await seed.SaveChangesAsync();
        }
        var step = instance.Steps.Single(s => s.Order == 1);
        var request = NotificationStepConfig.ToRequest(Executed("""{"channel":"Email"}""")) with
        {
            InstanceId = instance.Id,
            InstanceStepId = step.Id,
        };

        var outcomes = new[]
        {
            WorkflowNotificationOutcome.NotSent(request, "le lead n'a pas consenti au contact par e-mail."),
            WorkflowNotificationOutcome.Queued(request, "2 utilisateurs prévenus dans l'application."),
            WorkflowNotificationOutcome.Queued(request, "2 utilisateurs prévenus dans l'application."), // redelivered
        };
        foreach (var outcome in outcomes)
        {
            var context = Substitute.For<ConsumeContext<WorkflowNotificationOutcome>>();
            context.Message.Returns(outcome);
            await new WorkflowNotificationOutcomeConsumer(_factory.CreateContext(), NullLogger<WorkflowNotificationOutcomeConsumer>.Instance)
                .Consume(context);
        }

        await using var verify = _factory.CreateContext();
        verify.WorkflowAuditEntries
            .Where(a => a.InstanceId == instance.Id)
            .Select(a => new { a.EventCode, a.Comment, a.FromStateId })
            .ToList()
            .Should().BeEquivalentTo(new[]
            {
                new { EventCode = EventCodes.NotificationNotSent, Comment = (string?)"Non envoyé : le lead n'a pas consenti au contact par e-mail.", FromStateId = (Guid?)step.StepDefinitionId },
                new { EventCode = EventCodes.NotificationSent, Comment = (string?)"2 utilisateurs prévenus dans l'application.", FromStateId = (Guid?)step.StepDefinitionId },
            });
    }

    [Fact]
    public async Task The_transition_action_notification_now_tells_the_role()
    {
        var bus = Substitute.For<IBus>();
        var action = WorkflowAction.Create(Guid.NewGuid(), Guid.NewGuid(), ActionType.SendNotification,
            """{"title":"Dossier à revoir","body":"Un dossier attend","recipientRoleCode":"BranchManager"}""", 1);
        var context = new WorkflowContext { TenantId = _tenantId, InstanceId = Guid.NewGuid(), EntityType = "Lead", EntityId = Guid.NewGuid() };
        var executor = new Sankore.Modules.Workflow.Infrastructure.Actions.Executors.SendNotificationExecutor(bus);

        await executor.ExecuteAsync(action, context, default);
        await executor.ExecuteAsync(action, context, default);

        var published = bus.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<WorkflowNotificationRequested>().ToList();
        published.Should().HaveCount(2);
        published.Select(r => r.IdempotencyKey).Distinct().Should().ContainSingle("a replayed action must not notify twice");
        published[0].Recipients.Should().Equal(WorkflowNotificationRecipients.Role);
        published[0].RoleCodes.Should().Equal("BranchManager");
        published[0].Message.Should().Be("Un dossier attend");
    }

    private NotificationStepExecutedEvent Executed(string? configJson)
        => new(Guid.NewGuid(), _tenantId, "Lead", Guid.NewGuid(), Guid.NewGuid(), "Prévenir", configJson);

    private ApproveStepHandler ApproveHandler()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenantId);
        user.Id.Returns(Guid.NewGuid());
        user.Roles.Returns(["branchmanager"]);
        return new(_factory.CreateContext(), user, Substitute.For<IBus>(),
            Substitute.For<IRuleEvaluator>(), new ConditionEvaluator(),
            Substitute.For<IActionExecutorDispatcher>());
    }

    private AutomaticStepsCatchUp CatchUp(IBus bus)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton(Substitute.For<IRuleEvaluator>());
        services.AddSingleton<IConditionEvaluator>(new ConditionEvaluator());
        services.AddSingleton(Substitute.For<IActionExecutorDispatcher>());
        services.AddSingleton(bus);
        var provider = services.BuildServiceProvider();
        return new AutomaticStepsCatchUp(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHostApplicationLifetime>(),
            NullLogger<AutomaticStepsCatchUp>.Instance);
    }
}
