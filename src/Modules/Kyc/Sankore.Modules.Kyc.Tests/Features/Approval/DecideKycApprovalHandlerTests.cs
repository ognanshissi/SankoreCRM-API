namespace Sankore.Modules.Kyc.Tests.Features.Approval;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Features.Approval.Decide;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// KYC-B-05 — the circuit is climbed in order, by somebody other than the person who submitted it.
/// </summary>
public sealed class DecideKycApprovalHandlerTests : IDisposable
{
    private const string Motive = "Pièce d'identité illisible sur la photo du recto.";

    private readonly Guid _tenantId = Guid.NewGuid();

    /// <summary>The agent who collected and submitted the file — the one who may never approve it.</summary>
    private readonly Guid _submitterId = Guid.NewGuid();

    private readonly Guid _approverId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly DecideKycApprovalHandler _handler;

    public DecideKycApprovalHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Id.Returns(_approverId);

        _handler = new DecideKycApprovalHandler(
            _db, _currentUser, _publisher, _clock,
            NullLogger<DecideKycApprovalHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>
    /// A file where the verification has already run: submitted by <c>_submitterId</c>, scored, and
    /// sitting in Validating with its circuit written — exactly the state a decision arrives in.
    /// </summary>
    private async Task<KycFile> SeedAsync(
        KycVigilanceLevel vigilance = KycVigilanceLevel.Standard,
        bool fullEvidence = false)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, _submitterId, _clock, vigilance);

        file.SubmitForVerification(_submitterId, _clock);
        file.RecordVerification(85, KycConfidenceLevel.High, _clock);
        _db.KycFiles.Add(file);

        foreach (var level in Levels(vigilance))
            _db.KycApprovalSteps.Add(KycApprovalStep.Pending(_tenantId, file.Id, level, _clock));

        if (fullEvidence)
        {
            _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
                _tenantId, file.Id, "CNI", "v1:cipher", "blind-index", _clock,
                ocrFieldsJson: """{"surname":"KOUASSI"}"""));

            _db.KycFaceVerifications.Add(KycFaceVerification.Create(
                _tenantId, file.Id, attempt: 1, similarityScore: 0.94, isMatch: true, clock: _clock));
        }

        await _db.SaveChangesAsync();
        return file;
    }

    private static KycApprovalLevel[] Levels(KycVigilanceLevel vigilance)
        => vigilance == KycVigilanceLevel.High
            ? [KycApprovalLevel.Agent, KycApprovalLevel.BranchManager, KycApprovalLevel.ComplianceOfficer]
            : [KycApprovalLevel.Agent, KycApprovalLevel.BranchManager];

    private async Task<KycFile> ReloadFileAsync(Guid id)
    {
        using var verify = _factory.CreateContext();
        return await verify.KycFiles.AsNoTracking().SingleAsync(f => f.Id == id);
    }

    private async Task<Dictionary<KycApprovalLevel, KycApprovalStep>> ReloadStepsAsync(Guid fileId)
    {
        using var verify = _factory.CreateContext();
        return await verify.KycApprovalSteps.AsNoTracking()
            .Where(s => s.KycFileId == fileId)
            .ToDictionaryAsync(s => s.Level);
    }

    private Task<Result<DecideKycApprovalResult>> DecideAsync(
        Guid fileId, KycApprovalLevel level, KycApprovalDecision decision, string? comment = null)
        => _handler.Handle(
            new DecideKycApprovalCommand(fileId, level, decision, comment), CancellationToken.None);

    // ── Order ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_level_that_is_not_the_next_pending_one_cannot_be_decided()
    {
        // The branch manager cannot sign ahead of the agent, and a screen left open on a stale
        // circuit must be refused rather than sign a rung it was not showing.
        var file = await SeedAsync();

        var result = await DecideAsync(file.Id, KycApprovalLevel.BranchManager, KycApprovalDecision.Approved);

        result.Error.Should().Be(KycErrors.ApprovalOutOfOrder);

        var steps = await ReloadStepsAsync(file.Id);
        steps.Values.Should().OnlyContain(s => s.Decision == KycApprovalDecision.Pending);
        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public async Task An_unknown_file_is_reported_not_found()
    {
        await SeedAsync();

        var result = await DecideAsync(
            Guid.NewGuid(), KycApprovalLevel.Agent, KycApprovalDecision.Approved);

        result.Error.Should().Be(KycErrors.FileNotFound);
    }

    // ── Four eyes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_agent_who_submitted_the_file_cannot_approve_it()
    {
        // Enforced by KycFile.Approve, not re-implemented here: this test proves the handler calls
        // it and surfaces its code instead of recording the signature anyway.
        var file = await SeedAsync(fullEvidence: true);
        _currentUser.Id.Returns(_approverId);
        (await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved)).IsSuccess
            .Should().BeTrue();

        _currentUser.Id.Returns(_submitterId);
        var result = await DecideAsync(file.Id, KycApprovalLevel.BranchManager, KycApprovalDecision.Approved);

        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);

        var steps = await ReloadStepsAsync(file.Id);
        steps[KycApprovalLevel.BranchManager].Decision.Should().Be(KycApprovalDecision.Pending);
        steps[KycApprovalLevel.BranchManager].ApproverId.Should().BeNull();

        var untouched = await ReloadFileAsync(file.Id);
        untouched.Status.Should().Be(KycFileStatus.Validating);
        untouched.Tier.Should().Be(KycTier.None);
    }

    [Fact]
    public async Task The_agent_who_submitted_the_file_cannot_reject_it_either()
    {
        var file = await SeedAsync();
        _currentUser.Id.Returns(_submitterId);

        var result = await DecideAsync(
            file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Rejected, Motive);

        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);

        // The step is left Pending: a step is evidence and cannot be un-decided, so it must never
        // record a transition the aggregate refused.
        var steps = await ReloadStepsAsync(file.Id);
        steps[KycApprovalLevel.Agent].Decision.Should().Be(KycApprovalDecision.Pending);
        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.Validating);
        _publisher.ReceivedCalls().Should().BeEmpty();
    }

    // ── The three decisions ─────────────────────────────────────────────────

    [Fact]
    public async Task A_rejection_at_the_first_level_closes_the_file_and_leaves_the_later_steps_alone()
    {
        // The first person who says no is the answer. Making the file climb the remaining rungs to
        // be refused again would only delay what the customer is told.
        var file = await SeedAsync(KycVigilanceLevel.High);

        var result = await DecideAsync(
            file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Rejected, Motive);

        result.IsSuccess.Should().BeTrue();
        result.Value.CircuitCompleted.Should().BeTrue();

        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.Rejected);

        var steps = await ReloadStepsAsync(file.Id);
        steps[KycApprovalLevel.Agent].Decision.Should().Be(KycApprovalDecision.Rejected);
        steps[KycApprovalLevel.Agent].Comment.Should().Be(Motive);
        steps[KycApprovalLevel.Agent].ApproverId.Should().Be(_approverId);
        steps[KycApprovalLevel.BranchManager].Decision.Should().Be(KycApprovalDecision.Pending);
        steps[KycApprovalLevel.ComplianceOfficer].Decision.Should().Be(KycApprovalDecision.Pending);

        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycRejectedEvent>(e => e.Reason == Motive && e.CustomerEntityId == file.CustomerId),
            Arg.Any<CancellationToken>());

        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<KycValidatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_complement_request_sends_the_file_back_and_announces_nothing()
    {
        // The file goes back to the agent inside M02; the customer's record elsewhere has not
        // changed, and announcing it would be telling every module about our own paperwork.
        var file = await SeedAsync();

        var result = await DecideAsync(
            file.Id, KycApprovalLevel.Agent, KycApprovalDecision.ComplementRequired, Motive);

        result.IsSuccess.Should().BeTrue();
        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.ComplementRequired);
        (await ReloadStepsAsync(file.Id))[KycApprovalLevel.Agent].Decision
            .Should().Be(KycApprovalDecision.ComplementRequired);

        _publisher.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Approving_a_middle_level_records_the_signature_and_leaves_the_file_in_validation()
    {
        var file = await SeedAsync(KycVigilanceLevel.High);

        var result = await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved);

        result.IsSuccess.Should().BeTrue();
        result.Value.CircuitCompleted.Should().BeFalse();

        var stillWaiting = await ReloadFileAsync(file.Id);
        stillWaiting.Status.Should().Be(KycFileStatus.Validating);
        stillWaiting.Tier.Should().Be(KycTier.None);

        (await ReloadStepsAsync(file.Id))[KycApprovalLevel.Agent].Decision
            .Should().Be(KycApprovalDecision.Approved);

        // Nothing is announced before the last signature: M01 would activate a client the branch
        // manager has not yet seen.
        _publisher.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Approving_the_last_level_validates_the_file_and_publishes_both_events()
    {
        var file = await SeedAsync(fullEvidence: true);

        (await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved))
            .IsSuccess.Should().BeTrue();

        var result = await DecideAsync(
            file.Id, KycApprovalLevel.BranchManager, KycApprovalDecision.Approved);

        result.IsSuccess.Should().BeTrue();
        result.Value.CircuitCompleted.Should().BeTrue();

        var validated = await ReloadFileAsync(file.Id);
        validated.Status.Should().Be(KycFileStatus.Full);
        validated.Tier.Should().Be(KycTier.Full);
        validated.ValidatedAt.Should().NotBeNull();

        // The contract M01 already consumes to activate the client — no new one was invented.
        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycValidatedEvent>(e => e.CustomerEntityId == validated.CustomerId),
            Arg.Any<CancellationToken>());

        // PreviousTier is the tier BEFORE the approval: None on a first validation, which is what
        // M03 and M07 use to tell an initial grant from a change.
        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycTierChangedEvent>(e =>
                e.PreviousTier == nameof(KycTier.None) && e.CurrentTier == nameof(KycTier.Full)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_read_document_and_a_matched_face_the_file_is_validated_as_simplified()
    {
        // The compliance rule: the FULL tier needs both halves of the identity check to have
        // actually happened. Everything else is capped.
        var file = await SeedAsync();

        await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved);
        var result = await DecideAsync(
            file.Id, KycApprovalLevel.BranchManager, KycApprovalDecision.Approved);

        result.IsSuccess.Should().BeTrue();

        var validated = await ReloadFileAsync(file.Id);
        validated.Tier.Should().Be(KycTier.Simplified);
        validated.Status.Should().Be(KycFileStatus.Simplified);
    }

    [Fact]
    public async Task A_face_comparison_that_did_not_match_does_not_buy_the_full_tier()
    {
        var file = await SeedAsync(fullEvidence: true);

        using (var seed = _factory.CreateContext())
        {
            var mismatched = await seed.KycFaceVerifications.AsTracking()
                .SingleAsync(v => v.KycFileId == file.Id);
            seed.KycFaceVerifications.Remove(mismatched);
            seed.KycFaceVerifications.Add(KycFaceVerification.Create(
                _tenantId, file.Id, attempt: 2, similarityScore: 0.31, isMatch: false, clock: _clock));
            await seed.SaveChangesAsync();
        }

        await DecideAsync(file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved);
        await DecideAsync(file.Id, KycApprovalLevel.BranchManager, KycApprovalDecision.Approved);

        (await ReloadFileAsync(file.Id)).Tier.Should().Be(KycTier.Simplified);
    }
}

/// <summary>
/// KYC-B-05 — opening the circuit, once, whatever the verification outcome does on its way here.
/// </summary>
public sealed class StartKycApprovalHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();
    private readonly IWorkflowModule _workflow = Substitute.For<IWorkflowModule>();
    private readonly StartKycApprovalHandler _handler;

    public StartKycApprovalHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _settings
            .GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>())
            .Returns(2);

        _workflow
            .StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(Guid.NewGuid()));

        _handler = new StartKycApprovalHandler(
            _db, new KycApprovalCircuit(_settings), _workflow, _clock,
            NullLogger<StartKycApprovalHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private async Task<KycFile> SeedAsync(KycVigilanceLevel vigilance = KycVigilanceLevel.Standard)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, _agentId, _clock, vigilance);

        file.SubmitForVerification(_agentId, _clock);
        file.RecordVerification(85, KycConfidenceLevel.High, _clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private Task<Result<StartKycApprovalResult>> StartAsync(Guid fileId)
        => _handler.Handle(
            new StartKycApprovalCommand(_tenantId, fileId, _agentId), CancellationToken.None);

    private async Task<List<KycApprovalStep>> StepsAsync(Guid fileId)
    {
        using var verify = _factory.CreateContext();
        return await verify.KycApprovalSteps.AsNoTracking()
            .Where(s => s.KycFileId == fileId)
            .ToListAsync();
    }

    [Fact]
    public async Task The_circuit_is_written_once_however_many_times_the_outcome_is_replayed()
    {
        // A redelivered verification event, a Hangfire retry, two agents on the same file: a
        // second set of Pending steps would mean the same rung signed twice.
        var file = await SeedAsync();

        var first = await StartAsync(file.Id);
        var second = await StartAsync(file.Id);

        first.Value.AlreadyStarted.Should().BeFalse();
        second.Value.AlreadyStarted.Should().BeTrue();
        second.Value.Levels.Should().Equal(first.Value.Levels);

        (await StepsAsync(file.Id)).Should().HaveCount(2);

        // And one workflow, not two: the second run must not put a duplicate task in front of the
        // branch manager.
        await _workflow.Received(1).StartWorkflowAsync(
            Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_steps_are_created_pending_in_ladder_order()
    {
        var file = await SeedAsync(KycVigilanceLevel.High);

        var result = await StartAsync(file.Id);

        result.Value.Levels.Should().Equal(
            nameof(KycApprovalLevel.Agent),
            nameof(KycApprovalLevel.BranchManager),
            nameof(KycApprovalLevel.ComplianceOfficer));

        var steps = await StepsAsync(file.Id);
        steps.Should().HaveCount(3);
        steps.Should().OnlyContain(s => s.Decision == KycApprovalDecision.Pending);
        steps.Should().OnlyContain(s => s.TenantId == _tenantId);
    }

    [Fact]
    public async Task A_workflow_that_refuses_to_start_does_not_block_the_circuit()
    {
        // M12 records the circuit; it does not create it, and it enforces no self-approval rule of
        // its own. A tenant with no KycFile template must still be able to validate a file.
        _workflow
            .StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<Guid>("WORKFLOW_TEMPLATE_NOT_FOUND"));

        var file = await SeedAsync();

        var result = await StartAsync(file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.WorkflowInstanceId.Should().BeNull();
        (await StepsAsync(file.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_workflow_module_that_throws_does_not_block_the_circuit_either()
    {
        _workflow
            .StartWorkflowAsync(Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<Guid>>>(_ => throw new InvalidOperationException("M12 is down"));

        var file = await SeedAsync();

        var result = await StartAsync(file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.WorkflowInstanceId.Should().BeNull();
        (await StepsAsync(file.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unknown_file_is_reported_not_found()
    {
        var result = await StartAsync(Guid.NewGuid());

        result.Error.Should().Be(KycErrors.FileNotFound);
        await _workflow.DidNotReceive().StartWorkflowAsync(
            Arg.Any<WorkflowStartRequest>(), Arg.Any<CancellationToken>());
    }
}
