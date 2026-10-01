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
    private async Task<KycFile> SeedAsync()
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, _submitter, TimeProvider.System,
            vigilanceLevel: KycVigilanceLevel.High);

        file.SubmitForVerification(_submitter, TimeProvider.System);
        file.RecordVerification(85, KycConfidenceLevel.High, TimeProvider.System);

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
}
