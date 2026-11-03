namespace Sankore.Modules.Workflow.Tests.Features.Analytics;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Analytics.GetWorkflowHealth;
using Sankore.Modules.Workflow.Infrastructure.Conditions;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// « Santé des workflows » rebuilt « à traiter d'abord » (2026-11-03): what needs someone, named;
/// one line per circuit whatever its versions; and a delay counts as missed when it was — the old
/// screen only counted TimedOut, which escalation no longer produces, so every circuit read 0 %.
/// </summary>
public sealed class WorkflowHealthTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();

    public WorkflowHealthTests()
    {
        _factory = new(_tenantId);
        _administration.GetRoleLabelsAsync(_tenantId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BranchManager"] = "Chef d'agence" });
        _administration.GetUserNamesAsync(_tenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Late_and_waiting_steps_come_first_and_a_circuit_reads_as_one_line()
    {
        var v1 = WorkflowTemplate.Create(_tenantId, "Lead", "Dossier crédit", Guid.NewGuid());
        v1.AddStep(1, "Visite terrain", approverRoleCode: "BranchManager", timeoutHours: 24);
        v1.AddStep(2, "Comité", approverRoleCode: "BranchManager");
        var v2 = v1.CreateDraft(Guid.NewGuid());
        v2.Activate();
        await SeedAsync(v1, v2);

        var late = await StartAsync(v2);                    // step 1 still open, 24 h delay
        var waiting = await StartAsync(v2, approveFirst: true); // at step 2, which has no delay

        var health = await HealthAsync(hoursLater: 50);

        health.Attention.Select(a => (a.InstanceId, a.Kind)).Should().Equal((late, "Overdue"), (waiting, "Stuck"));
        var first = health.Attention[0];
        first.StepName.Should().Be("Visite terrain");
        first.ApproverRole.Should().Be("Chef d'agence");
        first.OverdueHours.Should().BeApproximately(26, 0.5);
        health.Attention[1].WaitingHours.Should().BeApproximately(50, 0.5, "waiting counts from when the step opened");

        var circuit = health.Circuits.Should().ContainSingle().Subject;
        circuit.TemplateId.Should().Be(v2.Id, "the active version is where the circuit opens");
        circuit.Versions.Should().Be(2);
        (circuit.Overdue, circuit.Stuck).Should().Be((1, 1));
        // The late step missed its delay; the one approved at once kept it.
        (circuit.SlaSteps, circuit.SlaBreached).Should().Be((2, 1));
        health.SlaKeptRate.Should().Be(0.5);
    }

    [Fact]
    public async Task A_step_escalated_then_decided_in_its_new_delay_still_missed_the_first()
    {
        var template = WorkflowTemplate.Create(_tenantId, "Lead", "Dossier crédit", Guid.NewGuid());
        template.AddStep(1, "Visite terrain", approverRoleCode: "BranchManager", timeoutHours: 24);
        template.AddStep(2, "Comité", approverRoleCode: "BranchManager");
        template.Activate();
        await SeedAsync(template);
        var instanceId = await StartAsync(template, approveFirst: true);
        await using (var db = _factory.CreateContext())
        {
            db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(_tenantId, instanceId, template.Steps.First().Id,
                template.Steps.First().Id, EventCodes.Escalated, Guid.Empty, "Délai dépassé : étape confiée à X."));
            await db.SaveChangesAsync();
        }

        var health = await HealthAsync(hoursLater: 1);

        health.SlaBreached.Should().Be(1);
        health.SlaKeptRate.Should().Be(0);
    }

    private async Task<WorkflowHealthDto> HealthAsync(int hoursLater)
    {
        var result = await new GetWorkflowHealthHandler(_factory.CreateContext(), new FixedTenantContext(_tenantId),
                new ShiftedClock(TimeSpan.FromHours(hoursLater)), administration: _administration)
            .Handle(new GetWorkflowHealthQuery(), default);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    private async Task SeedAsync(params WorkflowTemplate[] templates)
    {
        await using var db = _factory.CreateContext();
        foreach (var t in templates)
        {
            db.WorkflowTemplates.Add(t);
            db.WorkflowStepDefinitions.AddRange(t.Steps);
            db.WorkflowTransitions.AddRange(t.Transitions);
        }
        await db.SaveChangesAsync();
    }

    private async Task<Guid> StartAsync(WorkflowTemplate template, bool approveFirst = false)
    {
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());
        await using (var db = _factory.CreateContext())
        {
            db.WorkflowInstances.Add(instance);
            db.WorkflowInstanceSteps.AddRange(instance.Steps);
            await db.SaveChangesAsync();
        }
        if (approveFirst)
        {
            await using var db = _factory.CreateContext();
            var tracked = await db.WorkflowInstances.AsTracking().SingleAsync(i => i.Id == instance.Id);
            await db.Entry(tracked).Collection(i => i.Steps).LoadAsync();
            tracked.Approve(Guid.NewGuid(), null, template.Transitions, conditionEvaluator: new ConditionEvaluator());
            await db.SaveChangesAsync();
        }
        return instance.Id;
    }

    private sealed class ShiftedClock(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + by;
    }
}
