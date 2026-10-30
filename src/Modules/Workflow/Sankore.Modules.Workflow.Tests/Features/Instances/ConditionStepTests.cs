namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Infrastructure.Branching;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Workflow;
using Xunit;

/// <summary>
/// A « Condition » step compares one fact of the record, read when the step is reached, and the
/// circuit follows the branch it chose: the other branch's steps are skipped. Before, it waited
/// for an approval nobody was asked for, and both branches ran one after the other.
/// </summary>
public sealed class ConditionStepTests : IDisposable
{
    private const string ScoreAtLeast60 = """{"condition":{"nodeType":"leaf","field":"score","operator":"gte","value":"60"}}""";
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public ConditionStepTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private WorkflowTemplate Template(string? conditionJson = ScoreAtLeast60, bool activate = true)
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        t.AddStep(1, "Premier contact", approverRoleCode: "agent");
        t.AddStep(2, "Score suffisant ?", kind: StepKind.Condition, configJson: conditionJson);
        t.AddStep(3, "Comité", approverRoleCode: "branchmanager", configJson: """{"branch":"yes"}""");
        t.AddStep(4, "Relance", approverRoleCode: "agent", configJson: """{"branch":"no"}""");
        t.AddStep(5, "Clôture", approverRoleCode: "agent");
        if (activate) t.Activate();
        return t;
    }

    [Theory]
    [InlineData(true, 3, 4)]
    [InlineData(false, 4, 3)]
    public void The_branch_taken_runs_and_the_other_is_skipped_on_both_engine_paths(bool outcome, int taken, int skipped)
    {
        foreach (var stateMachine in new[] { true, false })
        {
            var template = Template();
            var transitions = stateMachine ? template.Transitions : null;
            var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());
            instance.Approve(Guid.NewGuid(), null, transitions, conditionEvaluator: new ConditionEvaluator());
            var condition = instance.Steps.Single(s => s.Order == 2);
            condition.Status.Should().Be(StepStatus.AwaitingApproval, "a condition waits for its consumer");

            instance.ResolveCondition(condition.Id, outcome, "raison", transitions,
                conditionEvaluator: new ConditionEvaluator()).Should().BeTrue();

            condition.Status.Should().Be(StepStatus.Executed);
            instance.Steps.Single(s => s.Order == skipped).Status.Should().Be(StepStatus.Skipped);
            instance.CurrentStepOrder.Should().Be(taken);

            instance.Approve(Guid.NewGuid(), null, transitions, conditionEvaluator: new ConditionEvaluator());
            instance.CurrentStepOrder.Should().Be(5, "the losing branch is stepped over, whichever comes first");
            instance.PendingAuditEntries.Should().Contain(a => a.EventCode == EventCodes.ConditionResolved && a.Comment == "raison");
        }
    }

    [Fact]
    public async Task Reaching_the_condition_evaluates_it_on_the_records_current_facts()
    {
        var template = Template();
        await SeedTemplateAsync(template);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());
        instance.Approve(Guid.NewGuid(), null, template.Transitions, conditionEvaluator: new ConditionEvaluator());
        await using (var db = _factory.CreateContext())
        {
            db.WorkflowInstances.Add(instance);
            db.WorkflowInstanceSteps.AddRange(instance.Steps);
            await db.SaveChangesAsync();
        }
        var conditionId = instance.Steps.Single(s => s.Order == 2).Id;

        await using (var db = _factory.CreateContext())
            db.OutboxMessages.Should().ContainSingle(m => m.EventType == typeof(ConditionStepReached).AssemblyQualifiedName);

        var context = Substitute.For<ConsumeContext<ConditionStepReached>>();
        context.Message.Returns(new ConditionStepReached(_tenantId, instance.Id, conditionId));
        await new ConditionStepConsumer(Services(score: 72).GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConditionStepConsumer>.Instance).Consume(context);

        await using var verify = _factory.CreateContext();
        verify.WorkflowInstances.Single(i => i.Id == instance.Id).CurrentStepOrder.Should().Be(3);
        verify.WorkflowAuditEntries.Should().Contain(a => a.EventCode == EventCodes.ConditionResolved
            && a.Comment == "Score du lead ≥ 60 : oui (valeur : 72) → branche Oui.");
    }

    [Theory]
    [InlineData(null, true, "n'a pas de condition")]
    [InlineData("""{"condition":{"nodeType":"leaf","field":"revenu","operator":"gt","value":"1"}}""", true, "champ inconnu")]
    [InlineData("""{"condition":{"nodeType":"leaf","field":"score","operator":"gte","value":""}}""", true, "valeur vide")]
    [InlineData(ScoreAtLeast60, false, "ne fournit pas de données")]
    [InlineData(ScoreAtLeast60, true, null)]
    public async Task A_condition_that_cannot_be_evaluated_is_not_activated(string? conditionJson, bool withProvider, string? refusal)
    {
        var template = Template(conditionJson, activate: false);
        await SeedTemplateAsync(template);

        var result = await new ActivateTemplateHandler(_factory.CreateContext(), withProvider ? [Provider(0)] : [])
            .Handle(new ActivateTemplateCommand(template.Id), default);

        if (refusal is null) result.IsSuccess.Should().BeTrue(result.Error);
        else result.Error.Should().Contain("« Score suffisant ? »").And.Contain(refusal);
    }

    private static IContextProvider Provider(double score)
    {
        var provider = Substitute.For<IContextProvider>();
        provider.EntityType.Returns("Lead");
        provider.Fields.Returns([ContextField.Number("score", "Score du lead")]);
        provider.BuildAsync(default, default, default).ReturnsForAnyArgs(
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["score"] = score });
        return provider;
    }

    private async Task SeedTemplateAsync(WorkflowTemplate template)
    {
        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(template);
        seed.WorkflowStepDefinitions.AddRange(template.Steps);
        seed.WorkflowTransitions.AddRange(template.Transitions);
        await seed.SaveChangesAsync();
    }

    private ServiceProvider Services(double score)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _factory.CreateContext());
        services.AddSingleton(Substitute.For<IRuleEvaluator>());
        services.AddSingleton<IConditionEvaluator>(new ConditionEvaluator());
        services.AddSingleton(Substitute.For<IActionExecutorDispatcher>());
        services.AddSingleton(Substitute.For<IBus>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Provider(score));
        services.AddScoped<InstanceRunner>();
        return services.BuildServiceProvider();
    }
}
