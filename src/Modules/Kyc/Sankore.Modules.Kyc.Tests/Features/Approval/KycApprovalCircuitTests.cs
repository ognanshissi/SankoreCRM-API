namespace Sankore.Modules.Kyc.Tests.Features.Approval;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Xunit;

/// <summary>
/// KYC-B-05 — the graduated approach: who has to sign, and why.
/// </summary>
public sealed class KycApprovalCircuitTests
{
    private const int MaxFaceAttempts = 2;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();
    private readonly KycApprovalCircuit _circuit;

    public KycApprovalCircuitTests()
    {
        _settings
            .GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>())
            .Returns(MaxFaceAttempts);

        _circuit = new KycApprovalCircuit(_settings);
    }

    private KycFile File(KycVigilanceLevel vigilance = KycVigilanceLevel.Standard)
        => KycFile.Open(_tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), _clock, vigilance);

    [Fact]
    public async Task A_low_risk_file_is_signed_by_the_agent_alone()
    {
        // An explicit arbitrage by the product owner, departing from the original wording of
        // KYC-B-05. Four eyes are not given up: DecideKycApprovalHandler refuses the submitter on
        // every rung, so the signing agent is never the one who built the file. What is given up
        // is the second pair of eyes on a low-risk file.
        var levels = await _circuit.ResolveAsync(
            File(KycVigilanceLevel.Low), CancellationToken.None);

        levels.Should().Equal(KycApprovalLevel.Agent);
    }

    [Fact]
    public async Task A_standard_file_is_still_counter_signed_by_the_branch_manager()
    {
        var levels = await _circuit.ResolveAsync(
            File(KycVigilanceLevel.Standard), CancellationToken.None);

        levels.Should().Equal(KycApprovalLevel.Agent, KycApprovalLevel.BranchManager);
    }

    [Fact]
    public async Task A_high_vigilance_file_ends_with_the_compliance_officer()
    {
        var levels = await _circuit.ResolveAsync(File(KycVigilanceLevel.High), CancellationToken.None);

        levels.Should().Equal(
            KycApprovalLevel.Agent, KycApprovalLevel.BranchManager, KycApprovalLevel.ComplianceOfficer);
    }

    [Fact]
    public async Task A_suspected_duplicate_reaches_compliance_even_at_a_lowered_vigilance()
    {
        // The flag raises the level to High, and an officer may lower it again afterwards
        // (KYC-B-04 leaves the two independent). The suspicion itself must still bring compliance
        // in — the circuit does not read it off the vigilance level.
        var file = File();
        file.FlagDuplicateSuspected(_clock);
        file.SetVigilanceLevel(KycVigilanceLevel.Standard, _clock);

        var levels = await _circuit.ResolveAsync(file, CancellationToken.None);

        levels.Should().Contain(KycApprovalLevel.ComplianceOfficer);
        levels.Should().Equal(
            KycApprovalLevel.Agent, KycApprovalLevel.BranchManager, KycApprovalLevel.ComplianceOfficer);
    }

    [Fact]
    public async Task At_the_tenant_threshold_the_branch_manager_is_required_even_on_a_low_risk_file()
    {
        // Two failed face comparisons are a signal about the CAPTURE, not about the customer: the
        // rule is read from the tenant's own face-match-max-attempts and applies whatever the
        // vigilance level. It does not promote the file to compliance — nothing suggests risk.
        var file = File(KycVigilanceLevel.Low);
        file.RecordFaceMatchAttempt(_clock);
        file.RecordFaceMatchAttempt(_clock);
        file.FaceMatchAttempts.Should().Be(MaxFaceAttempts);

        var levels = await _circuit.ResolveAsync(file, CancellationToken.None);

        levels.Should().ContainSingle(l => l == KycApprovalLevel.BranchManager);
        levels.Should().NotContain(KycApprovalLevel.ComplianceOfficer);

        // Read from the tenant's parameter and not from a constant in the code: an IMF may raise
        // or lower the number of attempts it tolerates.
        await _settings.Received().GetIntAsync(
            _tenantId, KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Below_the_threshold_the_circuit_is_the_one_the_vigilance_alone_dictates()
    {
        // One failed comparison is not two: a low-risk file keeps its single rung.
        var file = File(KycVigilanceLevel.Low);
        file.RecordFaceMatchAttempt(_clock);

        var levels = await _circuit.ResolveAsync(file, CancellationToken.None);

        levels.Should().Equal(KycApprovalLevel.Agent);
    }

    [Fact]
    public async Task A_level_is_never_returned_twice()
    {
        // Every clause adds into a set: the unique index on (file, level) would refuse a repeat,
        // and a circuit that cannot be written is a file that can never be validated.
        var file = File(KycVigilanceLevel.High);
        file.FlagDuplicateSuspected(_clock);
        file.RecordFaceMatchAttempt(_clock);
        file.RecordFaceMatchAttempt(_clock);

        var levels = await _circuit.ResolveAsync(file, CancellationToken.None);

        levels.Should().OnlyHaveUniqueItems();
        levels.Should().BeInAscendingOrder();
    }
}
