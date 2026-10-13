namespace Sankore.Modules.Kyc.Tests.Features.Routing;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews.RaiseReview;
using Sankore.Modules.Kyc.Features.Routing.Controls;
using Sankore.Modules.Kyc.Features.Routing.Controls.RecordControl;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The after-the-fact control of unattended approvals (KRM-B-11).
///
/// <para>
/// What is worth pinning here is not the drawing — a 10% sample is a random draw and a test that
/// asserted one outcome would be flaky by construction — but the two ENDS of the percentage, and
/// the properties of the verdict: once only, a finding required to say "no", a review raised, and
/// the one person who may not perform the control.
/// </para>
/// </summary>
public sealed class KycAutoApprovalControlTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _officerId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly ISender _sender = Substitute.For<ISender>();

    public KycAutoApprovalControlTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _sender.Send(Arg.Any<RaiseKycReviewCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new RaiseKycReviewResult(
                Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow))));
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private KycAutoApprovalSampler Sampler() =>
        new(_db, _clock, NullLogger<KycAutoApprovalSampler>.Instance);

    private KycFile ApprovedFile()
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, _agentId, _clock,
            agencyId: Guid.NewGuid(), productLineId: Guid.NewGuid(),
            responsibleAgentId: _agentId);

        file.SubmitForVerification(Guid.Empty, _clock);
        file.MarkRemotelyCollected(_clock);
        file.RecordVerification(95, KycConfidenceLevel.High, _clock);
        file.ApproveAutomatically(KycTier.Simplified, _clock);
        return file;
    }

    private RecordKycAutoApprovalControlHandler Handler(Guid officer)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(officer);
        user.TenantId.Returns(_tenantId);

        return new RecordKycAutoApprovalControlHandler(
            _db, _sender, user, _clock,
            NullLogger<RecordKycAutoApprovalControlHandler>.Instance);
    }

    private async Task<KycAutoApprovalControl> SeedControlAsync()
    {
        var file = ApprovedFile();
        _db.KycFiles.Add(file);

        Sampler().Draw(file, KycTier.Simplified, policyVersion: 1, samplePct: 100);
        await _db.SaveChangesAsync(CancellationToken.None);

        return await _db.KycAutoApprovalControls
            .SingleAsync(c => c.KycFileId == file.Id, CancellationToken.None);
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_sample_of_zero_draws_nothing()
    {
        // An institution's right, though an unwise one — and the value a tenant that has not
        // configured the feature would reach if the default were ever changed to 0.
        var file = ApprovedFile();
        _db.KycFiles.Add(file);

        Sampler().Draw(file, KycTier.Simplified, policyVersion: 1, samplePct: 0);
        await _db.SaveChangesAsync(CancellationToken.None);

        (await _db.KycAutoApprovalControls.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task A_sample_of_a_hundred_draws_every_approval()
    {
        // How a tenant runs the automatic mode under full review while it builds confidence in it.
        var file = ApprovedFile();
        _db.KycFiles.Add(file);

        Sampler().Draw(file, KycTier.Full, policyVersion: 7, samplePct: 100);
        await _db.SaveChangesAsync(CancellationToken.None);

        var control = await _db.KycAutoApprovalControls.SingleAsync(CancellationToken.None);

        control.KycFileId.Should().Be(file.Id);
        control.CustomerId.Should().Be(_customerId);
        control.Status.Should().Be(KycAutoApprovalControlStatus.Pending);
        control.GrantedTier.Should().Be(KycTier.Full);

        // The question a regulator asks is "under which rules was this relationship entered into
        // without a human", so the answer has to survive the rules changing.
        control.PolicyVersion.Should().Be(7);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(150)]
    public async Task An_out_of_range_percentage_is_clamped_rather_than_refused(int pct)
    {
        // This is not the place a misconfiguration should surface — an approval must not be lost
        // over a sampling row — and clamping errs towards controlling MORE.
        var file = ApprovedFile();
        _db.KycFiles.Add(file);

        Sampler().Draw(file, KycTier.Simplified, policyVersion: 1, samplePct: pct);
        await _db.SaveChangesAsync(CancellationToken.None);

        (await _db.KycAutoApprovalControls.CountAsync(CancellationToken.None))
            .Should().Be(pct < 0 ? 0 : 1);
    }

    [Fact]
    public void A_tier_of_None_is_refused_outright()
    {
        // Not a state the automatic path can produce — ApproveAutomatically refuses it — so a
        // control of "an approval that granted nothing" is a bug, not a row.
        var act = () => KycAutoApprovalControl.Sample(
            _tenantId, Guid.NewGuid(), _customerId, KycTier.None, 1, _clock);

        act.Should().Throw<DomainException>();
    }

    // ── The verdict ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_compliant_verdict_is_recorded_and_raises_no_review()
    {
        var control = await SeedControlAsync();

        var result = await Handler(_officerId).Handle(
            new RecordKycAutoApprovalControlCommand(control.Id, Compliant: true, Finding: null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(KycAutoApprovalControlStatus.Compliant));
        result.Value.RaisedReviewId.Should().BeNull();

        await _sender.DidNotReceive().Send(
            Arg.Any<RaiseKycReviewCommand>(), Arg.Any<CancellationToken>());

        var reloaded = await _db.KycAutoApprovalControls
            .AsNoTracking().SingleAsync(c => c.Id == control.Id, CancellationToken.None);

        // Who checked, and when — which is what a regulator asks, not only whether somebody did.
        reloaded.ReviewedBy.Should().Be(_officerId);
        reloaded.ReviewedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_non_compliant_verdict_needs_a_finding()
    {
        // "Non conforme" alone tells the colleague picking up the review nothing to act on.
        var control = await SeedControlAsync();

        var result = await Handler(_officerId).Handle(
            new RecordKycAutoApprovalControlCommand(control.Id, Compliant: false, Finding: "  "),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.AutoApprovalControlFindingRequired);

        var reloaded = await _db.KycAutoApprovalControls
            .AsNoTracking().SingleAsync(c => c.Id == control.Id, CancellationToken.None);

        reloaded.Status.Should().Be(KycAutoApprovalControlStatus.Pending);
    }

    [Fact]
    public async Task A_non_compliant_verdict_opens_a_review_and_links_it()
    {
        var control = await SeedControlAsync();

        var result = await Handler(_officerId).Handle(
            new RecordKycAutoApprovalControlCommand(
                control.Id, Compliant: false, Finding: "Pièce illisible, le score ne tenait pas."),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(KycAutoApprovalControlStatus.NonCompliant));
        result.Value.RaisedReviewId.Should().NotBeNull();

        // The review's reason is a FIXED sentence and not the officer's finding: the two have
        // different audiences, and a review reason is read by whoever picks it up.
        await _sender.Received(1).Send(
            Arg.Is<RaiseKycReviewCommand>(c =>
                c.KycFileId == control.KycFileId
                && c.Reason == RecordKycAutoApprovalControlHandler.ReviewReason),
            Arg.Any<CancellationToken>());

        var reloaded = await _db.KycAutoApprovalControls
            .AsNoTracking().SingleAsync(c => c.Id == control.Id, CancellationToken.None);

        reloaded.RaisedReviewId.Should().Be(result.Value.RaisedReviewId);
    }

    [Fact]
    public async Task The_verdict_STANDS_when_the_review_cannot_be_raised()
    {
        // A control recorded without its review is a true statement about what happened; a verdict
        // rolled back because a review could not be scheduled would lose the finding itself, which
        // is the one thing that cannot be reconstructed.
        _sender.Send(Arg.Any<RaiseKycReviewCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<RaiseKycReviewResult>(KycErrors.FileNotOpen));

        var control = await SeedControlAsync();

        var result = await Handler(_officerId).Handle(
            new RecordKycAutoApprovalControlCommand(
                control.Id, Compliant: false, Finding: "Identité douteuse."),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Null, and the response says so — which is what lets the screen tell the officer to raise
        // one by hand instead of hiding the gap.
        result.Value.RaisedReviewId.Should().BeNull();

        var reloaded = await _db.KycAutoApprovalControls
            .AsNoTracking().SingleAsync(c => c.Id == control.Id, CancellationToken.None);

        reloaded.Status.Should().Be(KycAutoApprovalControlStatus.NonCompliant);
        reloaded.Finding.Should().Be("Identité douteuse.");
    }

    [Fact]
    public async Task A_control_is_recorded_ONCE()
    {
        // A recorded finding is evidence that a control was performed. Re-recording it would
        // rewrite who found what — the same reason a decided approval step refuses a second
        // decision. An officer who changes their mind raises a review on the file.
        var control = await SeedControlAsync();

        await Handler(_officerId).Handle(
            new RecordKycAutoApprovalControlCommand(control.Id, Compliant: true, Finding: null),
            CancellationToken.None);

        var second = await Handler(Guid.NewGuid()).Handle(
            new RecordKycAutoApprovalControlCommand(
                control.Id, Compliant: false, Finding: "En fait non."),
            CancellationToken.None);

        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be(KycErrors.AutoApprovalControlAlreadyRecorded);

        var reloaded = await _db.KycAutoApprovalControls
            .AsNoTracking().SingleAsync(c => c.Id == control.Id, CancellationToken.None);

        reloaded.Status.Should().Be(KycAutoApprovalControlStatus.Compliant);
        reloaded.ReviewedBy.Should().Be(_officerId);
    }

    [Fact]
    public async Task The_files_own_responsible_agent_cannot_control_its_automatic_approval()
    {
        // The four-eyes rule this feature CAN enforce. LastSubmittedBy is SYSTEM on these files
        // and is therefore useless as an anchor, so the guard is about the file instead: the
        // person whose conversion produced it must not be the one certifying that a machine was
        // right to validate it unattended.
        var control = await SeedControlAsync();

        var result = await Handler(_agentId).Handle(
            new RecordKycAutoApprovalControlCommand(control.Id, Compliant: true, Finding: null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);

        var reloaded = await _db.KycAutoApprovalControls
            .AsNoTracking().SingleAsync(c => c.Id == control.Id, CancellationToken.None);

        reloaded.Status.Should().Be(KycAutoApprovalControlStatus.Pending);
    }

    [Fact]
    public async Task SYSTEM_can_never_perform_a_control()
    {
        // The system auditing itself is the one thing this table exists to make impossible, so the
        // aggregate throws rather than failing: it is a programming error, not a business outcome.
        var control = await SeedControlAsync();
        var tracked = await _db.KycAutoApprovalControls
            .AsTracking().SingleAsync(c => c.Id == control.Id, CancellationToken.None);

        var act = () => tracked.Record(
            compliant: true, reviewedBy: Guid.Empty, finding: null, clock: _clock);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public async Task A_control_of_another_tenant_is_simply_absent()
    {
        var control = await SeedControlAsync();

        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_officerId);
        user.TenantId.Returns(Guid.NewGuid());

        await using var other = new TestKycDbContextFactory(user.TenantId).CreateContext();

        var result = await new RecordKycAutoApprovalControlHandler(
                other, _sender, user, _clock,
                NullLogger<RecordKycAutoApprovalControlHandler>.Instance)
            .Handle(
                new RecordKycAutoApprovalControlCommand(control.Id, true, null),
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.AutoApprovalControlNotFound);
    }
}
