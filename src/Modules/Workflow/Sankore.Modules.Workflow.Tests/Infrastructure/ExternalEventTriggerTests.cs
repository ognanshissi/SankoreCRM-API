namespace Sankore.Modules.Workflow.Tests.Infrastructure;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Infrastructure.Rules;
using Sankore.Modules.Workflow.Infrastructure.Triggers;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Workflow;
using Xunit;

/// <summary>
/// A partner's event (webhooks lot 6) starts the circuits whose <c>ExternalEvent</c> trigger names
/// it — and only those: a partner naming its event like one of the CRM's own must not start the
/// CRM's journeys, and a CRM event must not start a partner's.
/// </summary>
public sealed class ExternalEventTriggerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly ServiceProvider _services;

    public ExternalEventTriggerTests()
    {
        _factory = new TestWorkflowDbContextFactory(_tenantId);
        _services = new ServiceCollection()
            .AddScoped(_ => _factory.CreateContext())
            .AddSingleton<IRuleEvaluator, RuleEvaluator>()
            .AddSingleton<IConditionEvaluator, ConditionEvaluator>()
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task An_external_event_starts_only_the_external_triggers_of_its_name()
    {
        var external = await SeedAsync("Lead", TriggerType.ExternalEvent, "appointment.booked");
        await SeedAsync("Lead", TriggerType.EntityEvent, "appointment.booked");

        await ConsumeAsync(new WorkflowTriggerSignal(_tenantId, "Lead", Guid.NewGuid(), "appointment.booked") { IsExternal = true });

        await using var db = _factory.CreateContext();
        (await db.WorkflowInstances.Select(i => i.TemplateId).ToListAsync()).Should().Equal(external.Id);
    }

    [Fact]
    public async Task A_crm_event_does_not_start_an_external_trigger_of_the_same_name()
    {
        var crm = await SeedAsync("Lead", TriggerType.EntityEvent, "LEAD_CAPTURED");
        await SeedAsync("Lead", TriggerType.ExternalEvent, "LEAD_CAPTURED");

        await ConsumeAsync(new WorkflowTriggerSignal(_tenantId, "Lead", Guid.NewGuid(), "LEAD_CAPTURED"));

        await using var db = _factory.CreateContext();
        (await db.WorkflowInstances.Select(i => i.TemplateId).ToListAsync()).Should().Equal(crm.Id);
    }

    [Fact]
    public async Task An_external_event_on_a_lead_never_starts_a_circuit_of_another_entity()
    {
        await SeedAsync("Opportunity", TriggerType.ExternalEvent, "file.completed");

        await ConsumeAsync(new WorkflowTriggerSignal(_tenantId, "Lead", Guid.NewGuid(), "file.completed") { IsExternal = true });

        await using var db = _factory.CreateContext();
        (await db.WorkflowInstances.CountAsync()).Should().Be(0);
    }

    private async Task<WorkflowTemplate> SeedAsync(string entityType, TriggerType type, string eventName)
    {
        var template = WorkflowTemplate.Create(_tenantId, entityType, $"Circuit {Guid.NewGuid():N}", Guid.NewGuid());
        template.AddStep(1, "Rappeler le prospect");
        template.Activate();

        await using var db = _factory.CreateContext();
        db.WorkflowTemplates.Add(template);
        db.WorkflowStepDefinitions.AddRange(template.Steps);
        db.WorkflowTransitions.AddRange(template.Transitions);
        db.WorkflowTriggers.Add(WorkflowTrigger.Create(_tenantId, template.Id, type, eventName));
        await db.SaveChangesAsync();
        return template;
    }

    private Task ConsumeAsync(WorkflowTriggerSignal signal)
    {
        var context = Substitute.For<ConsumeContext<WorkflowTriggerSignal>>();
        context.Message.Returns(signal);
        context.CancellationToken.Returns(CancellationToken.None);

        return new WorkflowTriggerConsumer(
                _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkflowTriggerConsumer>.Instance)
            .Consume(context);
    }
}
