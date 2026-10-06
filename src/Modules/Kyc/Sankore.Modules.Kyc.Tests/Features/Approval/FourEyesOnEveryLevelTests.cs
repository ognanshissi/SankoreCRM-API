namespace Sankore.Modules.Kyc.Tests.Features.Approval;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval.Decide;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// `KycFile.Approve` and `Reject` refuse the submitter, which covers a rejection and the FINAL
/// approval. An intermediate approval touches no aggregate method, so the rule had a hole exactly
/// where it is least visible: the agent who submitted a file could sign its first rung and the
/// circuit would carry their name. KYC-B-05 admits no such exception.
/// </summary>
public sealed class FourEyesOnEveryLevelTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _submitter = Guid.NewGuid();
    private readonly TestKycDbContextFactory _factory;

    public FourEyesOnEveryLevelTests() => _factory = new TestKycDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private ICurrentUser User(Guid id)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(id);
        user.TenantId.Returns(_tenantId);
        return user;
    }

    /// <summary>A file in Validating, submitted by <see cref="_submitter"/>, with a 3-rung circuit.</summary>
    /// <param name="manuallyValidatedBy">
    /// When set, the file reaches validation on this person's word instead of on a score — and they
    /// become the SECOND actor the rule must refuse on every rung.
    /// </param>
    private async Task<KycFile> SeedAsync(Guid? manuallyValidatedBy = null)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, _submitter, TimeProvider.System,
            vigilanceLevel: KycVigilanceLevel.High);

        file.SubmitForVerification(_submitter, TimeProvider.System);

        if (manuallyValidatedBy is { } validator)
        {
            file.ManuallyValidate(
                validator, "Service biométrique indisponible, pièces contrôlées à la main",
                TimeProvider.System).IsSuccess.Should().BeTrue();
        }
        else
        {
            file.RecordVerification(85, KycConfidenceLevel.High, TimeProvider.System);
        }

        await using var db = _factory.CreateContext();
        db.KycFiles.Add(file);
        foreach (var level in new[]
                 {
                     KycApprovalLevel.Agent,
                     KycApprovalLevel.BranchManager,
                     KycApprovalLevel.ComplianceOfficer,
                 })
        {
            db.KycApprovalSteps.Add(KycApprovalStep.Pending(_tenantId, file.Id, level, TimeProvider.System));
        }

        await db.SaveChangesAsync();
        return file;
    }

    private DecideKycApprovalHandler Handler(Guid approver) =>
        new(_factory.CreateContext(), User(approver),
            Substitute.For<IEventPublisher>(), TimeProvider.System,
            NullLogger<DecideKycApprovalHandler>.Instance);

    [Fact]
    public async Task The_submitter_cannot_sign_the_first_rung_of_their_own_file()
    {
        var file = await SeedAsync();

        var result = await Handler(_submitter).Handle(
            new DecideKycApprovalCommand(
                file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved, "RAS"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);

        // And the signature was not recorded: a decided step cannot be un-decided.
        await using var db = _factory.CreateContext();
        var step = await db.KycApprovalSteps
            .FirstAsync(s => s.KycFileId == file.Id && s.Level == KycApprovalLevel.Agent);

        step.Decision.Should().Be(KycApprovalDecision.Pending);
        step.ApproverId.Should().BeNull();
    }

    [Fact]
    public async Task Nor_send_their_own_file_back_for_complement()
    {
        // ComplementRequired touches no aggregate guard either — same hole, same fix.
        var file = await SeedAsync();

        var result = await Handler(_submitter).Handle(
            new DecideKycApprovalCommand(
                file.Id, KycApprovalLevel.Agent, KycApprovalDecision.ComplementRequired, "Photo floue"),
            CancellationToken.None);

        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);
    }

    [Fact]
    public async Task Somebody_else_signs_the_same_rung_without_trouble()
    {
        var file = await SeedAsync();

        var result = await Handler(Guid.NewGuid()).Handle(
            new DecideKycApprovalCommand(
                file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved, "Vérifié"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var step = await db.KycApprovalSteps
            .FirstAsync(s => s.KycFileId == file.Id && s.Level == KycApprovalLevel.Agent);

        step.Decision.Should().Be(KycApprovalDecision.Approved);
        step.ApproverId.Should().NotBe(_submitter);
    }

    // ── the second anchor: whoever validated the evidence by hand ───────────

    /// <summary>
    /// A manually validated file has no biometric score behind it, so the ladder is the only
    /// control left — and a ladder signed by the person who opened it is not a control. The hole is
    /// the same shape as the submitter's: an intermediate rung reaches no aggregate method.
    /// </summary>
    [Fact]
    public async Task The_manual_validator_cannot_sign_the_first_rung_either()
    {
        var validator = Guid.NewGuid();
        var file = await SeedAsync(manuallyValidatedBy: validator);

        var result = await Handler(validator).Handle(
            new DecideKycApprovalCommand(
                file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved, "RAS"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);

        await using var db = _factory.CreateContext();
        var step = await db.KycApprovalSteps
            .FirstAsync(s => s.KycFileId == file.Id && s.Level == KycApprovalLevel.Agent);

        step.Decision.Should().Be(KycApprovalDecision.Pending);
        step.ApproverId.Should().BeNull();
    }

    [Fact]
    public async Task Nor_refuse_the_file_they_validated_by_hand()
    {
        var validator = Guid.NewGuid();
        var file = await SeedAsync(manuallyValidatedBy: validator);

        var result = await Handler(validator).Handle(
            new DecideKycApprovalCommand(
                file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Rejected, "Finalement non"),
            CancellationToken.None);

        result.Error.Should().Be(KycErrors.SelfApprovalForbidden);
    }

    [Fact]
    public async Task Somebody_else_can_still_sign_a_manually_validated_file()
    {
        // The rule must narrow who signs, not make the file unsignable — that would turn the
        // escape hatch into a dead end.
        var file = await SeedAsync(manuallyValidatedBy: Guid.NewGuid());

        var result = await Handler(Guid.NewGuid()).Handle(
            new DecideKycApprovalCommand(
                file.Id, KycApprovalLevel.Agent, KycApprovalDecision.Approved, "Vérifié"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
