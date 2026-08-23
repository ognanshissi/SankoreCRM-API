namespace Sankore.Modules.Workflow.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class WorkflowInstanceTests
{
    private static readonly Guid _tenantId = Guid.NewGuid();
    private static readonly Guid _userId = Guid.NewGuid();
    private static readonly Guid _approver = Guid.NewGuid();

    private static WorkflowTemplate BuildActiveTemplate(int stepCount = 2)
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", _userId);
        for (var i = 1; i <= stepCount; i++)
            t.AddStep(i, $"Step {i}", approverRoleCode: "manager");
        t.Activate();
        return t;
    }

    // ── S1: Start creates steps and moves to step 1 ───────────────────────

    [Fact]
    public void Start_creates_steps_and_is_in_progress()
    {
        var template = BuildActiveTemplate(2);
        var entityId = Guid.NewGuid();

        var instance = WorkflowInstance.Start(template, entityId, _userId);

        instance.Status.Should().Be(WorkflowStatus.InProgress);
        instance.CurrentStepOrder.Should().Be(1);
        instance.Steps.Should().HaveCount(2);
        instance.Steps.First(s => s.Order == 1).Status.Should().Be(StepStatus.AwaitingApproval);
        instance.Steps.First(s => s.Order == 2).Status.Should().Be(StepStatus.Pending);
    }

    // ── S2: Cannot start from inactive template ───────────────────────────

    [Fact]
    public void Start_throws_when_template_is_inactive()
    {
        var t = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", _userId);
        t.AddStep(1, "Step 1");
        // NOT activated

        var act = () => WorkflowInstance.Start(t, Guid.NewGuid(), _userId);
        act.Should().Throw<DomainException>().WithMessage("*inactive*");
    }

    // ── S3: Approve first step advances to step 2 ─────────────────────────

    [Fact]
    public void Approve_first_step_advances_to_second_step()
    {
        var template = BuildActiveTemplate(2);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), _userId);

        instance.Approve(_approver, "looks good");

        instance.Status.Should().Be(WorkflowStatus.InProgress);
        instance.CurrentStepOrder.Should().Be(2);
        instance.Steps.First(s => s.Order == 1).Status.Should().Be(StepStatus.Approved);
        instance.Steps.First(s => s.Order == 2).Status.Should().Be(StepStatus.AwaitingApproval);
    }

    // ── S4: Approve last step completes the workflow ───────────────────────

    [Fact]
    public void Approve_last_step_completes_workflow_and_raises_event()
    {
        var template = BuildActiveTemplate(1);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), _userId);

        instance.Approve(_approver);

        instance.Status.Should().Be(WorkflowStatus.Completed);
        instance.CompletedAt.Should().NotBeNull();
        instance.DomainEvents.Should().ContainSingle(e => e is WorkflowCompletedEvent);
    }

    // ── S5: Reject stops workflow and raises event ────────────────────────

    [Fact]
    public void Reject_stops_workflow_and_raises_rejected_event()
    {
        var template = BuildActiveTemplate(2);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), _userId);

        instance.Reject(_approver, "not compliant");

        instance.Status.Should().Be(WorkflowStatus.Rejected);
        instance.Steps.First(s => s.Order == 1).Status.Should().Be(StepStatus.Rejected);
        instance.DomainEvents.Should().ContainSingle(e => e is WorkflowRejectedEvent);
    }

    // ── S6: Cancel after start ────────────────────────────────────────────

    [Fact]
    public void Cancel_moves_to_cancelled_status()
    {
        var template = BuildActiveTemplate(1);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), _userId);

        instance.Cancel();

        instance.Status.Should().Be(WorkflowStatus.Cancelled);
    }

    // ── S7: Cancel already completed throws ───────────────────────────────

    [Fact]
    public void Cancel_throws_when_workflow_already_completed()
    {
        var template = BuildActiveTemplate(1);
        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), _userId);
        instance.Approve(_approver);

        var act = () => instance.Cancel();
        act.Should().Throw<DomainException>().WithMessage("*already finished*");
    }
}
