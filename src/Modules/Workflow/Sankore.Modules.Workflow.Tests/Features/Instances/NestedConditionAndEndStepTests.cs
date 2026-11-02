namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Features.Templates.ActivateTemplate;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Workflow;
using Xunit;

/// <summary>
/// Conditions at any depth and « Fin » steps (product owner, 2026-11-02). The circuit under test:
/// <code>
/// 1 Accueil
/// 2 Montant élevé ?            (c1)
///   Oui: 3 Analyse
///        4 Client connu ?      (c2)
///          Oui: 5 Fin validée — Qualifier le lead
///          Non: 6 Pièces
///        7 Comité
///   Non: 8 Fin refusée — Marquer le lead perdu
/// 9 Clôture
/// </code>
/// </summary>
public sealed class NestedConditionAndEndStepTests : IDisposable
{
    private const string Leaf = "\"condition\":{\"nodeType\":\"leaf\",\"field\":\"score\",\"operator\":\"gte\",\"value\":\"60\"}";
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public NestedConditionAndEndStepTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private WorkflowTemplate Template(bool activate = true, Action<WorkflowTemplate>? alter = null, string entity = "Lead")
    {
        var t = WorkflowTemplate.Create(_tenantId, entity, "Circuit imbriqué", Guid.NewGuid());
        t.AddStep(1, "Accueil", approverRoleCode: "agent");
        t.AddStep(2, "Montant élevé ?", kind: StepKind.Condition, configJson: $$"""{"node":"c1",{{Leaf}}}""");
        t.AddStep(3, "Analyse", approverRoleCode: "agent", configJson: """{"branch":"yes","parent":"c1"}""");
        t.AddStep(4, "Client connu ?", kind: StepKind.Condition,
            configJson: $$"""{"node":"c2","branch":"yes","parent":"c1",{{Leaf}}}""");
        t.AddStep(5, "Fin validée", kind: StepKind.End,
            configJson: """{"branch":"yes","parent":"c2","outcome":"approved","action":"lead.qualify"}""");
        t.AddStep(6, "Pièces", approverRoleCode: "agent", configJson: """{"branch":"no","parent":"c2"}""");
        t.AddStep(7, "Comité", approverRoleCode: "branchmanager", configJson: """{"branch":"yes","parent":"c1"}""");
        t.AddStep(8, "Fin refusée", kind: StepKind.End,
            configJson: """{"branch":"no","parent":"c1","outcome":"rejected","action":"lead.lose"}""");
        t.AddStep(9, "Clôture", approverRoleCode: "agent");
        alter?.Invoke(t);
        if (activate) t.Activate();
        return t;
    }

    private static (WorkflowInstance Instance, IReadOnlyCollection<WorkflowTransition>? Transitions) Started(
        WorkflowTemplate template, bool stateMachine)
    {
        var transitions = stateMachine ? template.Transitions : null;
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());
        instance.Approve(Guid.NewGuid(), null, transitions, conditionEvaluator: new ConditionEvaluator());
        return (instance, transitions);
    }

    private static void Resolve(WorkflowInstance instance, int order, bool outcome, IReadOnlyCollection<WorkflowTransition>? transitions)
        => instance.ResolveCondition(instance.Steps.Single(s => s.Order == order).Id, outcome, "raison", transitions,
            conditionEvaluator: new ConditionEvaluator()).Should().BeTrue();

    private static StepStatus StatusOf(WorkflowInstance instance, int order) => instance.Steps.Single(s => s.Order == order).Status;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_nested_condition_skips_only_its_own_losing_branch(bool stateMachine)
    {
        var (instance, transitions) = Started(Template(), stateMachine);

        Resolve(instance, 2, true, transitions);
        StatusOf(instance, 8).Should().Be(StepStatus.Skipped, "the « Non » branch of the outer condition");
        instance.CurrentStepOrder.Should().Be(3);

        instance.Approve(Guid.NewGuid(), null, transitions, conditionEvaluator: new ConditionEvaluator());
        Resolve(instance, 4, false, transitions);
        StatusOf(instance, 5).Should().Be(StepStatus.Skipped, "the « Oui » branch of the inner condition");
        instance.CurrentStepOrder.Should().Be(6);

        instance.Approve(Guid.NewGuid(), null, transitions, conditionEvaluator: new ConditionEvaluator());
        instance.CurrentStepOrder.Should().Be(7, "back in the outer « Oui » branch after the inner condition");

        instance.Approve(Guid.NewGuid(), null, transitions, conditionEvaluator: new ConditionEvaluator());
        instance.CurrentStepOrder.Should().Be(9, "the steps after the outer condition run once both branches are done");
        instance.Status.Should().Be(WorkflowStatus.InProgress);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_fin_inside_a_branch_ends_the_circuit_validated_and_asks_for_its_action(bool stateMachine)
    {
        var (instance, transitions) = Started(Template(), stateMachine);
        Resolve(instance, 2, true, transitions);
        instance.Approve(Guid.NewGuid(), null, transitions, conditionEvaluator: new ConditionEvaluator());

        Resolve(instance, 4, true, transitions);

        instance.Status.Should().Be(WorkflowStatus.Completed);
        StatusOf(instance, 5).Should().Be(StepStatus.Executed);
        new[] { 6, 7, 8, 9 }.Should().OnlyContain(o => StatusOf(instance, o) == StepStatus.Skipped);
        instance.DomainEvents.OfType<EndStepReachedEvent>().Should().ContainSingle(e => e.Action == WorkflowEndActions.LeadQualify && e.Approved);
        instance.DomainEvents.OfType<WorkflowCompletedEvent>().Should().ContainSingle();
        instance.PendingAuditEntries.Should().Contain(a => a.EventCode == EventCodes.Ended
            && a.Comment == "Fin du circuit : validé. Action demandée : Qualifier le lead.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_refusing_fin_ends_the_circuit_rejected(bool stateMachine)
    {
        var (instance, transitions) = Started(Template(), stateMachine);

        Resolve(instance, 2, false, transitions);

        instance.Status.Should().Be(WorkflowStatus.Rejected);
        new[] { 3, 4, 5, 6, 7, 9 }.Should().OnlyContain(o => StatusOf(instance, o) == StepStatus.Skipped);
        instance.DomainEvents.OfType<EndStepReachedEvent>().Should().ContainSingle(e => e.Action == WorkflowEndActions.LeadLose && !e.Approved);
        instance.DomainEvents.OfType<WorkflowRejectedEvent>().Should().ContainSingle();
        instance.DomainEvents.OfType<WorkflowCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task The_action_is_written_to_the_outbox_with_the_circuits_name()
    {
        var template = Template();
        await SeedAsync(template);
        var (instance, transitions) = Started(template, stateMachine: true);
        Resolve(instance, 2, false, transitions);

        await using (var db = _factory.CreateContext())
        {
            db.WorkflowInstances.Add(instance);
            db.WorkflowInstanceSteps.AddRange(instance.Steps);
            await db.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        await using var verify = _factory.CreateContext();
        var message = verify.OutboxMessages.Single(m => m.EventType == typeof(WorkflowEndActionRequested).AssemblyQualifiedName);
        message.PayloadJson.Should().Contain("lead.lose").And.Contain("Circuit imbriqu");
    }

    [Fact]
    public async Task An_instance_ended_where_no_tenant_is_resolved_still_saves_and_announces_its_action()
    {
        var template = Template();
        await SeedAsync(template);
        var (instance, transitions) = Started(template, stateMachine: true);
        await using (var db = _factory.CreateContext())
        {
            db.WorkflowInstances.Add(instance);
            db.WorkflowInstanceSteps.AddRange(instance.Steps);
            await db.SaveChangesAsync();
        }

        // A consumer has no request: reading the tenant throws, as CurrentUser does.
        var noTenant = Substitute.For<Sankore.Shared.Kernel.ITenantContext>();
        noTenant.CurrentTenantId.Returns(_ => throw new InvalidOperationException("No tenant resolved for the current context."));
        await using (var db = _factory.CreateContext(noTenant))
        {
            var tracked = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .SingleAsync(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(
                    Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsTracking(
                        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters(db.WorkflowInstances)),
                    i => i.Steps), i => i.Id == instance.Id);
            tracked.ResolveCondition(tracked.Steps.Single(s => s.Order == 2).Id, false, "raison", transitions,
                conditionEvaluator: new ConditionEvaluator()).Should().BeTrue();

            await db.SaveChangesAsync();
        }

        await using var verify = _factory.CreateContext();
        verify.OutboxMessages.Should().ContainSingle(m => m.EventType == typeof(WorkflowEndActionRequested).AssemblyQualifiedName);
    }

    [Fact]
    public async Task A_well_formed_tree_is_activated()
    {
        var template = Template(activate: false);
        await SeedAsync(template);

        var result = await new ActivateTemplateHandler(_factory.CreateContext(), [Provider()])
            .Handle(new ActivateTemplateCommand(template.Id), default);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Theory]
    [InlineData("after-end", "vient après la fin « Fin principale »")]
    [InlineData("unknown-parent", "condition qui n'existe plus")]
    [InlineData("wrong-entity", "ne s'applique pas à ce type de dossier")]
    [InlineData("interleaved", "séparée de sa condition")]
    public async Task A_broken_tree_is_not_activated(string defect, string refusal)
    {
        var template = Template(activate: false, alter: t =>
        {
            switch (defect)
            {
                case "after-end":
                    t.AddStep(10, "Fin principale", kind: StepKind.End, configJson: """{"outcome":"approved"}""");
                    t.AddStep(11, "Après la fin", approverRoleCode: "agent");
                    break;
                case "unknown-parent":
                    t.AddStep(10, "Orpheline", approverRoleCode: "agent", configJson: """{"branch":"yes","parent":"zz"}""");
                    break;
                case "wrong-entity":
                    t.AddStep(10, "Fin projet", kind: StepKind.End, configJson: """{"action":"project.win"}""");
                    break;
                case "interleaved":
                    t.AddStep(10, "Égarée", approverRoleCode: "agent", configJson: """{"branch":"yes","parent":"c2"}""");
                    break;
            }
        });
        await SeedAsync(template);

        var result = await new ActivateTemplateHandler(_factory.CreateContext(), [Provider()])
            .Handle(new ActivateTemplateCommand(template.Id), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain(refusal);
    }

    [Fact]
    public void A_circuit_saved_before_nesting_still_branches_by_contiguity()
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Ancien", Guid.NewGuid());
        t.AddStep(1, "Accueil", approverRoleCode: "agent");
        t.AddStep(2, "Condition", kind: StepKind.Condition, configJson: $$"""{{{Leaf}}}""");
        t.AddStep(3, "Oui", approverRoleCode: "agent", configJson: """{"branch":"yes"}""");
        t.AddStep(4, "Non", approverRoleCode: "agent", configJson: """{"branch":"no"}""");
        t.AddStep(5, "Suite", approverRoleCode: "agent");
        t.Activate();

        var (instance, transitions) = Started(t, stateMachine: true);
        Resolve(instance, 2, true, transitions);

        StatusOf(instance, 4).Should().Be(StepStatus.Skipped);
        StatusOf(instance, 5).Should().Be(StepStatus.Pending);
    }

    private static IContextProvider Provider()
    {
        var provider = Substitute.For<IContextProvider>();
        provider.EntityType.Returns("Lead");
        provider.Fields.Returns([ContextField.Number("score", "Score du lead")]);
        return provider;
    }

    private async Task SeedAsync(WorkflowTemplate template)
    {
        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(template);
        seed.WorkflowStepDefinitions.AddRange(template.Steps);
        seed.WorkflowTransitions.AddRange(template.Transitions);
        await seed.SaveChangesAsync();
    }
}
