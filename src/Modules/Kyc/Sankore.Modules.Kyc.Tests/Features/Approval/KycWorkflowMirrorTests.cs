namespace Sankore.Modules.Kyc.Tests.Features.Approval;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Features.Approval.Decide;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The link between M02's circuit and its M12 reflection.
///
/// <para>
/// Two things are being pinned, and the second matters more than the first. One: the instance is
/// told the ladder M02 actually resolved, so a low-risk file does not show rungs nobody will be
/// asked to sign. Two: <b>the mirror is never allowed to affect the decision</b> — M02 governs
/// compliance, M12 observes, and a traceability record that cannot be written must cost nothing.
/// </para>
/// </summary>
public sealed class KycWorkflowMirrorTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _submitterId = Guid.NewGuid();
    private readonly Guid _approverId = Guid.NewGuid();
    private readonly Guid _instanceId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IWorkflowModule _workflow = Substitute.For<IWorkflowModule>();
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();

    public KycWorkflowMirrorTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Id.Returns(_approverId);

        _settings.GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>())
            .Returns(2);

        _workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(_instanceId));

        _workflow.RecordDecisionAsync(Arg.Any<RecordWorkflowDecisionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private StartKycApprovalHandler StartHandler() =>
        new(_db, new KycApprovalCircuit(_settings), _workflow, _clock,
            NullLogger<StartKycApprovalHandler>.Instance);

    private DecideKycApprovalHandler DecideHandler() =>
        new(_db, _currentUser, Substitute.For<IEventPublisher>(), _workflow, _clock,
            NullLogger<DecideKycApprovalHandler>.Instance);

    /// <summary>A file in Validating, with its circuit opened through the real handler.</summary>
    private async Task<KycFile> SeedStartedAsync(
        KycVigilanceLevel vigilance = KycVigilanceLevel.Standard)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, _submitterId, _clock, vigilance);

        file.SubmitForVerification(_submitterId, _clock);
        file.RecordVerification(85, KycConfidenceLevel.High, _clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();

        var started = await StartHandler().Handle(
            new StartKycApprovalCommand(_tenantId, file.Id, _submitterId), CancellationToken.None);

        started.IsSuccess.Should().BeTrue();
        return file;
    }

    private async Task<KycFile> ReloadFileAsync(Guid id)
    {
        using var verify = _factory.CreateContext();
        return await verify.KycFiles.AsNoTracking().SingleAsync(f => f.Id == id);
    }

    private Task<Result<DecideKycApprovalResult>> DecideAsync(
        Guid fileId, KycApprovalLevel level, KycApprovalDecision decision, string? comment = null)
        => DecideHandler().Handle(
            new DecideKycApprovalCommand(fileId, level, decision, comment), CancellationToken.None);

    // ── the ladder M02 resolved is the ladder M12 is told ───────────────────

    [Theory]
    [InlineData(KycVigilanceLevel.Low, 1)]
    [InlineData(KycVigilanceLevel.Standard, 2)]
    [InlineData(KycVigilanceLevel.High, 3)]
    public async Task The_instance_is_told_exactly_the_rungs_the_circuit_resolved(
        KycVigilanceLevel vigilance, int expectedRungs)
    {
        await SeedStartedAsync(vigilance);

        var request = _workflow.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<WorkflowStartRequest>()
            .Single();

        request.EntityType.Should().Be("KycFile");
        request.RequiredStepOrders.Should().NotBeNull();
        request.RequiredStepOrders!.Should().Equal(Enumerable.Range(1, expectedRungs));
    }

    [Fact]
    public async Task The_instance_id_is_kept_on_the_file()
    {
        // It used to reach the result DTO and be dropped, so there was nothing to mirror onto.
        var file = await SeedStartedAsync();

        (await ReloadFileAsync(file.Id)).WorkflowInstanceId.Should().Be(_instanceId);
    }

    [Fact]
    public async Task A_tenant_with_no_template_simply_has_no_mirror()
    {
        _workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<Guid>("No active workflow template found for entity type 'KycFile'."));

        var file = await SeedStartedAsync();

        (await ReloadFileAsync(file.Id)).WorkflowInstanceId.Should().BeNull();

        // And the circuit is there regardless — that is the whole point of degrading.
        using var verify = _factory.CreateContext();
        verify.KycApprovalSteps.Where(s => s.KycFileId == file.Id).Should().NotBeEmpty();
    }

    // ── decisions are mirrored ──────────────────────────────────────────────

    [Fact]
    public async Task An_intermediate_approval_is_recorded_on_its_rung()
    {
        var file = await SeedStartedAsync();

        await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved, "Vérifié");

        await _workflow.Received(1).RecordDecisionAsync(
            Arg.Is<RecordWorkflowDecisionRequest>(r =>
                r.InstanceId == _instanceId
                && r.StepOrder == (int)KycApprovalLevel.Agent
                && r.Decision == WorkflowDecision.Approved
                && r.ActedByUserId == _approverId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rejection_is_recorded_as_a_rejection()
    {
        var file = await SeedStartedAsync();

        await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Rejected, "Pièce illisible");

        await _workflow.Received(1).RecordDecisionAsync(
            Arg.Is<RecordWorkflowDecisionRequest>(r => r.Decision == WorkflowDecision.Rejected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_complement_request_cancels_the_round_and_forgets_the_instance()
    {
        // The file goes back to its author, so this round is over. Clearing the id is what makes the
        // next pass open a fresh instance instead of continuing one that was interrupted.
        var file = await SeedStartedAsync();

        await DecideAsync(
            file.Id, KycApprovalLevel.Agent, KycApprovalDecision.ComplementRequired, "Photo floue");

        await _workflow.Received(1).RecordDecisionAsync(
            Arg.Is<RecordWorkflowDecisionRequest>(r => r.Decision == WorkflowDecision.Cancelled),
            Arg.Any<CancellationToken>());

        var reloaded = await ReloadFileAsync(file.Id);
        reloaded.Status.Should().Be(KycFileStatus.ComplementRequired);
        reloaded.WorkflowInstanceId.Should().BeNull();
    }

    [Fact]
    public async Task Re_entering_validation_opens_a_second_instance()
    {
        var file = await SeedStartedAsync();
        await DecideAsync(
            file.Id, KycApprovalLevel.Agent, KycApprovalDecision.ComplementRequired, "Photo floue");

        var second = Guid.NewGuid();
        _workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(second));

        // The steps already exist, so this is the AlreadyStarted path — the one that used to return
        // null and leave the next round with no mirror at all.
        var restarted = await StartHandler().Handle(
            new StartKycApprovalCommand(_tenantId, file.Id, _submitterId), CancellationToken.None);

        restarted.IsSuccess.Should().BeTrue();
        restarted.Value.AlreadyStarted.Should().BeTrue();
        restarted.Value.WorkflowInstanceId.Should().Be(second);
        (await ReloadFileAsync(file.Id)).WorkflowInstanceId.Should().Be(second);
    }

    [Fact]
    public async Task A_replayed_start_does_not_open_a_second_instance_for_one_round()
    {
        var file = await SeedStartedAsync();

        await StartHandler().Handle(
            new StartKycApprovalCommand(_tenantId, file.Id, _submitterId), CancellationToken.None);

        await _workflow.Received(1).StartWorkflowAsync(
            Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>());
        (await ReloadFileAsync(file.Id)).WorkflowInstanceId.Should().Be(_instanceId);
    }

    // ── and the mirror never costs a decision ───────────────────────────────

    [Fact]
    public async Task A_workflow_module_that_throws_does_not_undo_the_decision()
    {
        // THE test of this feature. The decision is committed before the mirror runs, and a
        // compliance decision must never be rolled back because a traceability record failed.
        var file = await SeedStartedAsync();

        _workflow.RecordDecisionAsync(Arg.Any<RecordWorkflowDecisionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("M12 is down"));

        var result = await DecideAsync(
            file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved, "Vérifié");

        result.IsSuccess.Should().BeTrue("the circuit does not depend on its reflection");

        using var verify = _factory.CreateContext();
        var step = await verify.KycApprovalSteps.AsNoTracking()
            .SingleAsync(s => s.KycFileId == file.Id && s.Level == KycApprovalLevel.Agent);

        step.Decision.Should().Be(KycApprovalDecision.Approved, "the signature stands");
    }

    [Fact]
    public async Task A_refused_mirror_does_not_undo_the_decision_either()
    {
        // What a blown SLA produces: M12 reports the instance already terminal.
        var file = await SeedStartedAsync();

        _workflow.RecordDecisionAsync(Arg.Any<RecordWorkflowDecisionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail("Workflow instance is already TimedOut and records nothing further."));

        var result = await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_complement_whose_mirror_fails_keeps_the_file_consistent()
    {
        // The id must NOT be cleared when the cancel did not happen: clearing it would open a second
        // instance next round while the first one is still running.
        var file = await SeedStartedAsync();

        _workflow.RecordDecisionAsync(Arg.Any<RecordWorkflowDecisionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail("instance not found"));

        var result = await DecideAsync(
            file.Id, KycApprovalLevel.Agent, KycApprovalDecision.ComplementRequired, "Photo floue");

        result.IsSuccess.Should().BeTrue();
        (await ReloadFileAsync(file.Id)).WorkflowInstanceId.Should().Be(
            _instanceId, "the cancel was refused, so the link still describes a live instance");
    }

    [Fact]
    public async Task A_file_with_no_mirror_records_nothing_and_still_decides()
    {
        _workflow.StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<Guid>("no template"));

        var file = await SeedStartedAsync();

        var result = await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved);

        result.IsSuccess.Should().BeTrue();
        await _workflow.DidNotReceive().RecordDecisionAsync(
            Arg.Any<RecordWorkflowDecisionRequest>(), Arg.Any<CancellationToken>());
    }
}
