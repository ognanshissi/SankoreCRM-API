namespace Sankore.Modules.Workflow.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Modules.Workflow.Tests.TestSupport;
using Xunit;

/// <summary>
/// The cross-module surface: what another module can start, and how it mirrors a decision it has
/// already taken onto the instance that tracks it.
///
/// <para>
/// The caller is M02, whose approval ladder is computed per file — one rung for a low-risk file,
/// three for a suspected duplicate. A template is a fixed list of steps, so the shape has to be
/// told rather than inferred, and the engine is not allowed to re-decide a compliance rule it
/// cannot see.
/// </para>
/// </summary>
public sealed class WorkflowModuleFacadeTests : IDisposable
{
    private const string EntityType = "KycFile";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _entityId = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly TestWorkflowDbContextFactory _factory;

    public WorkflowModuleFacadeTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private WorkflowModuleFacade Facade() => new(_factory.CreateContext());

    /// <summary>A three-rung active template, as the seeder produces.</summary>
    private async Task<WorkflowTemplate> SeedTemplateAsync(int steps = 3)
    {
        var template = WorkflowTemplate.Create(_tenantId, EntityType, "Validation KYC", Guid.Empty);
        for (var order = 1; order <= steps; order++)
            template.AddStep(order, $"Rung {order}");
        template.Activate();

        await using var seed = _factory.CreateContext();
        seed.WorkflowTemplates.Add(template);
        await seed.SaveChangesAsync();
        return template;
    }

    private async Task<WorkflowInstance> ReloadAsync(Guid instanceId)
    {
        await using var verify = _factory.CreateContext();
        return await verify.WorkflowInstances
            .IgnoreQueryFilters()
            .Include(i => i.Steps)
            .SingleAsync(i => i.Id == instanceId);
    }

    private Task<Sankore.Shared.Kernel.Result<Guid>> StartAsync(IReadOnlyCollection<int>? orders) =>
        Facade().StartWorkflowAsync(
            new WorkflowStartRequest(EntityType, _entityId, _actor, _tenantId, orders));

    // ── the ladder the caller asked for ─────────────────────────────────────

    [Fact]
    public async Task A_caller_that_names_one_rung_gets_the_others_skipped()
    {
        // M02's low-risk file: the agent signs alone. An instance showing a branch-manager step
        // awaiting a decision would be inviting one nobody will ever be asked for.
        await SeedTemplateAsync();

        var started = await StartAsync([1]);

        started.IsSuccess.Should().BeTrue();
        var instance = await ReloadAsync(started.Value);

        instance.Steps.Single(s => s.Order == 1).Status.Should().Be(StepStatus.AwaitingApproval);
        instance.Steps.Where(s => s.Order != 1).Should()
            .OnlyContain(s => s.Status == StepStatus.Skipped);
        instance.CurrentStepOrder.Should().Be(1);
    }

    [Fact]
    public async Task One_approval_completes_a_one_rung_instance()
    {
        await SeedTemplateAsync();
        var started = await StartAsync([1]);

        var recorded = await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 1, WorkflowDecision.Approved, _actor, "Vérifié"));

        recorded.IsSuccess.Should().BeTrue();
        (await ReloadAsync(started.Value)).Status.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task Naming_no_rung_at_all_is_refused_rather_than_completed()
    {
        // The hazard worth a test of its own: every step skipped leaves nothing to do, and an
        // instance with nothing to do completes ITSELF — reporting a fully approved entity nobody
        // ever signed for. A caller whose ladder does not match the template is a configuration
        // fault, not an approval.
        await SeedTemplateAsync();

        var started = await StartAsync([7, 8]);

        started.IsFailure.Should().BeTrue();
        started.Error.Should().Contain("7, 8");

        await using var verify = _factory.CreateContext();
        verify.WorkflowInstances.IgnoreQueryFilters().Should().BeEmpty("nothing was started");
    }

    [Fact]
    public async Task Naming_nothing_means_every_rung_applies()
    {
        await SeedTemplateAsync();

        var started = await StartAsync(null);

        var instance = await ReloadAsync(started.Value);
        instance.Steps.Should().NotContain(s => s.Status == StepStatus.Skipped);
    }

    [Fact]
    public async Task No_active_template_is_a_failure_and_not_an_exception()
    {
        // M02 degrades to a log line on this, so it must come back as a Result.
        (await StartAsync([1])).IsFailure.Should().BeTrue();
    }

    // ── mirroring a decision ────────────────────────────────────────────────

    [Fact]
    public async Task An_approval_advances_to_the_next_applicable_rung()
    {
        await SeedTemplateAsync();
        var started = await StartAsync([1, 3]);

        await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 1, WorkflowDecision.Approved, _actor));

        var instance = await ReloadAsync(started.Value);
        instance.Status.Should().Be(WorkflowStatus.InProgress);
        instance.CurrentStepOrder.Should().Be(3, "rung 2 does not apply to this entity");
    }

    [Fact]
    public async Task A_rejection_is_terminal()
    {
        await SeedTemplateAsync();
        var started = await StartAsync([1, 2]);

        await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 1, WorkflowDecision.Rejected, _actor, "Pièce illisible"));

        (await ReloadAsync(started.Value)).Status.Should().Be(WorkflowStatus.Rejected);
    }

    [Fact]
    public async Task A_cancellation_abandons_the_round_without_a_verdict()
    {
        // M02's complement request: the file goes back to its author, so this round is over and the
        // next pass gets a fresh instance.
        await SeedTemplateAsync();
        var started = await StartAsync([1, 2]);

        var recorded = await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 1, WorkflowDecision.Cancelled, _actor, "Photo floue"));

        recorded.IsSuccess.Should().BeTrue();
        (await ReloadAsync(started.Value)).Status.Should().Be(WorkflowStatus.Cancelled);
    }

    [Fact]
    public async Task Every_mirrored_decision_leaves_an_audit_entry()
    {
        await SeedTemplateAsync();
        var started = await StartAsync([1, 2]);

        await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 1, WorkflowDecision.Approved, _actor, "Vérifié"));

        await using var verify = _factory.CreateContext();
        var entries = await verify.WorkflowAuditEntries
            .IgnoreQueryFilters()
            .Where(e => e.InstanceId == started.Value && e.EventCode == EventCodes.Approve)
            .ToListAsync();

        entries.Should().ContainSingle()
            .Which.ActedByUserId.Should().Be(_actor, "the audit trail is the point of mirroring");
    }

    // ── what it refuses, so the caller can log and carry on ─────────────────

    [Fact]
    public async Task A_decision_on_the_wrong_rung_is_refused()
    {
        // The caller's ladder and this instance have drifted. Applying the decision to whichever
        // step happens to be open would hide that.
        await SeedTemplateAsync();
        var started = await StartAsync([1, 2]);

        var recorded = await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 2, WorkflowDecision.Approved, _actor));

        recorded.IsFailure.Should().BeTrue();
        (await ReloadAsync(started.Value)).CurrentStepOrder.Should().Be(1, "nothing moved");
    }

    [Fact]
    public async Task A_terminal_instance_records_nothing_further()
    {
        // Exactly what a blown SLA produces: SlaCheckerJob leaves the instance TimedOut, and every
        // later KYC decision has nowhere to land. It must fail, not throw.
        await SeedTemplateAsync();
        var started = await StartAsync([1, 2]);

        await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 1, WorkflowDecision.Rejected, _actor));

        var again = await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, _tenantId, 1, WorkflowDecision.Approved, _actor));

        again.IsFailure.Should().BeTrue();
        again.Error.Should().Contain("Rejected");
    }

    [Fact]
    public async Task An_unknown_instance_is_refused()
        => (await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
                Guid.NewGuid(), _tenantId, 1, WorkflowDecision.Approved, _actor)))
            .IsFailure.Should().BeTrue();

    [Fact]
    public async Task An_instance_of_another_tenant_is_not_found()
    {
        await SeedTemplateAsync();
        var started = await StartAsync([1]);

        var recorded = await Facade().RecordDecisionAsync(new RecordWorkflowDecisionRequest(
            started.Value, Guid.NewGuid(), 1, WorkflowDecision.Approved, _actor));

        recorded.IsFailure.Should().BeTrue();
    }
}
