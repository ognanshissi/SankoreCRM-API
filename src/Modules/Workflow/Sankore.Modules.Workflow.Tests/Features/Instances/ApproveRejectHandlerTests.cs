namespace Sankore.Modules.Workflow.Tests.Features.Instances;

using FluentAssertions;
using MassTransit;
using NSubstitute;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Modules.Workflow.Features.Instances.ApproveStep;
using Sankore.Modules.Workflow.Features.Instances.RejectStep;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class ApproveRejectHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;
    private readonly IBus _bus = Substitute.For<IBus>();

    public ApproveRejectHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private ICurrentUser BuildUser(params string[] roles)
    {
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        cu.Id.Returns(Guid.NewGuid());
        cu.Roles.Returns(roles.ToList());
        return cu;
    }

    private ApproveStepHandler ApproveHandler(ICurrentUser? cu = null) =>
        new(_factory.CreateContext(), cu ?? BuildUser("branchmanager"), _bus);

    private RejectStepHandler RejectHandler(ICurrentUser? cu = null) =>
        new(_factory.CreateContext(), cu ?? BuildUser("branchmanager"));

    /// Seeds a running instance (template with 2 steps) and returns its Id.
    private async Task<Guid> SeedRunningInstance()
    {
        var template = WorkflowTemplate.Create(_tenantId, "Lead", "Circuit", Guid.NewGuid());
        template.AddStep(1, "Step 1", approverRoleCode: "branchmanager");
        template.AddStep(2, "Step 2", approverRoleCode: "branchmanager");
        template.Activate();

        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(template);
        seed.WorkflowStepDefinitions.AddRange(template.Steps);
        await seed.SaveChangesAsync();

        var instance = WorkflowInstance.Start(template, Guid.NewGuid(), Guid.NewGuid());
        await using var seed2 = _factory.CreateContext();
        seed2.WorkflowInstances.Add(instance);
        seed2.WorkflowInstanceSteps.AddRange(instance.Steps);
        await seed2.SaveChangesAsync();
        return instance.Id;
    }

    // ── S1: Approve first step advances to step 2 ─────────────────────────

    [Fact]
    public async Task Approve_advances_to_next_step()
    {
        var instanceId = await SeedRunningInstance();

        var result = await ApproveHandler().Handle(
            new ApproveStepCommand(instanceId, "looks good"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var i = verify.WorkflowInstances.First(x => x.Id == instanceId);
        i.CurrentStepOrder.Should().Be(2);
        i.Status.Should().Be(WorkflowStatus.InProgress);
    }

    // ── S2: Approve last step completes and publishes integration event ────

    [Fact]
    public async Task Approve_last_step_completes_and_publishes_event()
    {
        var instanceId = await SeedRunningInstance();

        // Approve step 1 first
        await ApproveHandler().Handle(
            new ApproveStepCommand(instanceId, null), CancellationToken.None);

        // Approve step 2 (last) — should complete
        var result = await ApproveHandler().Handle(
            new ApproveStepCommand(instanceId, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        verify.WorkflowInstances.First(x => x.Id == instanceId).Status
            .Should().Be(WorkflowStatus.Completed);

        await _bus.Received(1).Publish(
            Arg.Is<WorkflowCompletedIntegrationEvent>(e => e.InstanceId == instanceId),
            Arg.Any<CancellationToken>());
    }

    // ── S3: Wrong role → rejected ────────────────────────────────────────

    [Fact]
    public async Task Approve_fails_when_user_missing_required_role()
    {
        var instanceId = await SeedRunningInstance();
        var wrongRoleUser = BuildUser("cashier"); // not "branchmanager"

        var result = await ApproveHandler(wrongRoleUser).Handle(
            new ApproveStepCommand(instanceId, null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("required role");
    }

    // ── S4: Reject stops the workflow ────────────────────────────────────

    [Fact]
    public async Task Reject_stops_workflow()
    {
        var instanceId = await SeedRunningInstance();

        var result = await RejectHandler().Handle(
            new RejectStepCommand(instanceId, "non-compliant"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        verify.WorkflowInstances.First(x => x.Id == instanceId).Status
            .Should().Be(WorkflowStatus.Rejected);
    }

    // ── S5: Instance not found ────────────────────────────────────────────

    [Fact]
    public async Task Approve_fails_when_instance_not_found()
    {
        var result = await ApproveHandler().Handle(
            new ApproveStepCommand(Guid.NewGuid(), null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    // ── S6: Instance already completed ────────────────────────────────────

    [Fact]
    public async Task Approve_fails_when_workflow_not_in_progress()
    {
        var instanceId = await SeedRunningInstance();
        // Complete it
        await ApproveHandler().Handle(new ApproveStepCommand(instanceId, null), CancellationToken.None);
        await ApproveHandler().Handle(new ApproveStepCommand(instanceId, null), CancellationToken.None);

        var result = await ApproveHandler().Handle(
            new ApproveStepCommand(instanceId, null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not in progress");
    }
}
