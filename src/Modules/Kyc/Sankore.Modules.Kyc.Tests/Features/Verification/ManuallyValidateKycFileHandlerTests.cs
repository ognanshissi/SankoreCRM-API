namespace Sankore.Modules.Kyc.Tests.Features.Verification;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Features.Verification.ManualValidation;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// KYC-F-02 — valider les pièces à la main quand la machine ne peut pas conclure.
///
/// <para>
/// The real <c>StartKycApprovalHandler</c> is wired in rather than substituted, because the thing
/// most likely to break here is invisible to a substitute: that handler re-reads the file through
/// the same context, which is NoTracking, so the manual validation has to be SAVED before the
/// circuit is started or the ladder is resolved from a file that does not yet look manually
/// validated — and comes back one rung too narrow, which the four-eyes anchor then makes
/// unsignable.
/// </para>
/// </summary>
public sealed class ManuallyValidateKycFileHandlerTests : IDisposable
{
    private const string Motive = "Service biométrique indisponible depuis 48h, pièces contrôlées visuellement.";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _validatorId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();
    private readonly IWorkflowModule _workflow = Substitute.For<IWorkflowModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly ISender _sender = Substitute.For<ISender>();

    private readonly ManuallyValidateKycFileHandler _handler;

    public ManuallyValidateKycFileHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Id.Returns(_validatorId);

        // The ladder-widening clause reads this; 2 is the shipped default.
        _settings.GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>())
            .Returns(2);

        var start = new StartKycApprovalHandler(
            _db, new KycApprovalCircuit(_settings), _workflow, _clock,
            NullLogger<StartKycApprovalHandler>.Instance);

        // Route StartKycApprovalCommand to the real handler, over the same context.
        _sender.Send(Arg.Any<StartKycApprovalCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => start.Handle(
                call.Arg<StartKycApprovalCommand>(), call.Arg<CancellationToken>()));

        _handler = new ManuallyValidateKycFileHandler(
            _db, _currentUser, _sender, _clock,
            NullLogger<ManuallyValidateKycFileHandler>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private async Task<KycFile> SeedAsync(
        KycFileStatus status = KycFileStatus.Verifying,
        KycVigilanceLevel vigilance = KycVigilanceLevel.Standard)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, _agentId, _clock, vigilance);

        if (status != KycFileStatus.Collecting)
            file.SubmitForVerification(_agentId, _clock);

        if (status == KycFileStatus.ComplementRequired)
            file.RecordVerification(10, KycConfidenceLevel.Rejected, _clock);
        else if (status == KycFileStatus.Validating)
            file.RecordVerification(85, KycConfidenceLevel.High, _clock);

        file.Status.Should().Be(status);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private Task<Result<ManuallyValidateKycFileResult>> ValidateAsync(Guid fileId, string reason = Motive)
        => _handler.Handle(
            new ManuallyValidateKycFileCommand(fileId, reason), CancellationToken.None);

    private async Task<KycFile> ReloadFileAsync(Guid id)
    {
        using var verify = _factory.CreateContext();
        return await verify.KycFiles.AsNoTracking().SingleAsync(f => f.Id == id);
    }

    private async Task<List<KycApprovalLevel>> ReloadLevelsAsync(Guid fileId)
    {
        using var verify = _factory.CreateContext();
        var steps = await verify.KycApprovalSteps.AsNoTracking()
            .Where(s => s.KycFileId == fileId)
            .ToListAsync();

        return [.. steps.Select(s => s.Level).Order()];
    }

    // ── the escape hatch works from both stuck states ───────────────────────

    [Theory]
    [InlineData(KycFileStatus.Verifying)]
    [InlineData(KycFileStatus.ComplementRequired)]
    public async Task A_stuck_file_reaches_validation_on_the_validators_word(KycFileStatus from)
    {
        var file = await SeedAsync(from);

        var result = await ValidateAsync(file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.FileStatus.Should().Be(nameof(KycFileStatus.Validating));

        var reloaded = await ReloadFileAsync(file.Id);
        reloaded.Status.Should().Be(KycFileStatus.Validating);
        reloaded.ManuallyValidatedBy.Should().Be(_validatorId);
        reloaded.ManualValidationReason.Should().Be(Motive);

        // Manual validation invents no score. On the Verifying path there never was one; on the
        // ComplementRequired path the rejected score that sent the file back is left exactly as it
        // was, so the file does not end up claiming the machine approved it.
        reloaded.ConfidenceScore.Should().Be(
            from == KycFileStatus.ComplementRequired ? 10 : null,
            "a human override must not fabricate a confidence score");
    }

    [Fact]
    public async Task The_approval_circuit_is_started_so_the_ladder_still_gates_the_file()
    {
        var file = await SeedAsync(KycFileStatus.ComplementRequired);

        var result = await ValidateAsync(file.Id);

        result.Value.ApprovalLevels.Should().NotBeEmpty();
        (await ReloadLevelsAsync(file.Id)).Should().NotBeEmpty(
            "a file reading 'in validation' must HAVE its circuit");
    }

    /// <summary>
    /// The regression this slice is most likely to grow: a low-risk file's ladder is the agent
    /// alone, so if the circuit were resolved before the manual validation was saved the ladder
    /// would come back as [Agent] — and the four-eyes anchor on ManuallyValidatedBy would make it
    /// unsignable by the only person it lists.
    /// </summary>
    [Fact]
    public async Task A_manually_validated_low_risk_file_still_gains_a_second_pair_of_eyes()
    {
        var file = await SeedAsync(KycFileStatus.Verifying, KycVigilanceLevel.Low);

        var result = await ValidateAsync(file.Id);

        result.IsSuccess.Should().BeTrue();
        (await ReloadLevelsAsync(file.Id)).Should().Equal(
            KycApprovalLevel.Agent, KycApprovalLevel.BranchManager);
    }

    [Fact]
    public async Task The_validator_is_not_recorded_as_the_submitter()
    {
        var file = await SeedAsync(KycFileStatus.Verifying);

        await ValidateAsync(file.Id);

        (await ReloadFileAsync(file.Id)).LastSubmittedBy.Should().Be(
            _agentId, "the agent who built the file stays the four-eyes anchor");
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(KycFileStatus.Collecting)]
    [InlineData(KycFileStatus.Validating)]
    public async Task A_file_that_is_not_stuck_is_refused(KycFileStatus from)
    {
        var file = await SeedAsync(from);

        var result = await ValidateAsync(file.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.InvalidTransition);
        (await ReloadLevelsAsync(file.Id)).Should().BeEmpty("no circuit for a refused move");
    }

    [Fact]
    public async Task A_validation_with_no_grounds_is_refused()
    {
        var file = await SeedAsync(KycFileStatus.Verifying);

        var result = await ValidateAsync(file.Id, "   ");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.ManualValidationReasonRequired);
        (await ReloadFileAsync(file.Id)).Status.Should().Be(KycFileStatus.Verifying);
    }

    [Fact]
    public async Task An_unknown_file_is_not_found()
        => (await ValidateAsync(Guid.NewGuid())).Error.Should().Be(KycErrors.FileNotFound);

    [Fact]
    public async Task Nothing_is_announced_to_other_modules()
    {
        // The service answered nothing, so there is no verification outcome to report; what M01
        // waits for is KycValidatedEvent, published when the ladder completes exactly as on the
        // scored path.
        var file = await SeedAsync(KycFileStatus.Verifying);

        await ValidateAsync(file.Id);

        _publisher.ReceivedCalls().Should().BeEmpty();
    }
}
