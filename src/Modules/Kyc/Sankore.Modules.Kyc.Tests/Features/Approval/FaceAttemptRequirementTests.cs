namespace Sankore.Modules.Kyc.Tests.Features.Approval;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Xunit;

/// <summary>
/// KYC-B-05: "Une soumission après deux échecs de comparaison faciale exige la validation du chef
/// d'agence, quel que soit le risque."
///
/// Today's ladder already puts the branch manager on every circuit — "risque faible ou standard :
/// agent puis chef d'agence" — so the clause in <see cref="KycApprovalCircuit"/> adds nobody, and
/// a reader could reasonably delete it as dead code. It is not dead: it is the only thing that
/// would still satisfy this requirement if the ladder were ever re-graduated so that a low-risk
/// file is signed by the agent alone.
///
/// These tests therefore assert the REQUIREMENT across every vigilance level, not the branch that
/// currently implements it. They keep passing if the ladder changes, and they fail if the clause
/// is removed while it changes.
/// </summary>
public sealed class FaceAttemptRequirementTests
{
    private const int MaxAttempts = 2;

    private static KycApprovalCircuit Circuit()
    {
        var settings = Substitute.For<IKycSettings>();
        settings.GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>())
            .Returns(MaxAttempts);
        return new KycApprovalCircuit(settings);
    }

    private static KycFile FileWith(KycVigilanceLevel vigilance, int faceAttempts)
    {
        var file = KycFile.Open(
            Guid.NewGuid(), Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(),
            TimeProvider.System, vigilanceLevel: vigilance);

        for (var i = 0; i < faceAttempts; i++)
            file.RecordFaceMatchAttempt(TimeProvider.System);

        return file;
    }

    [Theory]
    [InlineData(KycVigilanceLevel.Low)]
    [InlineData(KycVigilanceLevel.Standard)]
    [InlineData(KycVigilanceLevel.High)]
    public async Task At_the_threshold_the_branch_manager_signs_whatever_the_risk(
        KycVigilanceLevel vigilance)
    {
        var levels = await Circuit().ResolveAsync(
            FileWith(vigilance, MaxAttempts), CancellationToken.None);

        levels.Should().Contain(KycApprovalLevel.BranchManager,
            $"two failed face comparisons are a signal about the capture, not about a {vigilance} customer");
    }

    [Theory]
    [InlineData(KycVigilanceLevel.Low)]
    [InlineData(KycVigilanceLevel.Standard)]
    [InlineData(KycVigilanceLevel.High)]
    public async Task Past_the_threshold_too(KycVigilanceLevel vigilance)
    {
        var levels = await Circuit().ResolveAsync(
            FileWith(vigilance, MaxAttempts + 3), CancellationToken.None);

        levels.Should().Contain(KycApprovalLevel.BranchManager);
    }

    [Fact]
    public async Task The_threshold_comes_from_the_tenant_and_not_from_a_constant()
    {
        var settings = Substitute.For<IKycSettings>();
        settings.GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>())
            .Returns(5);

        var circuit = new KycApprovalCircuit(settings);

        // Four attempts is below a threshold of five: the rule must not fire on a hard-coded two.
        await circuit.ResolveAsync(FileWith(KycVigilanceLevel.Low, 4), CancellationToken.None);

        await settings.Received().GetIntAsync(
            Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_rule_never_promotes_a_file_to_compliance()
    {
        // A bad photo is not a fraud signal. Escalating to the compliance officer on it would bury
        // the real High-risk files under capture problems.
        var levels = await Circuit().ResolveAsync(
            FileWith(KycVigilanceLevel.Low, MaxAttempts), CancellationToken.None);

        levels.Should().NotContain(KycApprovalLevel.ComplianceOfficer);
    }

    [Fact]
    public async Task On_a_low_risk_file_the_clause_is_the_only_thing_that_adds_the_manager()
    {
        // Before the ladder was lightened, the branch manager was on every circuit and this clause
        // changed nothing — it looked like dead code. It is now the sole reason a low-risk file
        // gains a second signature, which is what makes the rule verifiable at all.
        var circuit = Circuit();

        var clean = await circuit.ResolveAsync(
            FileWith(KycVigilanceLevel.Low, faceAttempts: 0), CancellationToken.None);

        var struggling = await circuit.ResolveAsync(
            FileWith(KycVigilanceLevel.Low, faceAttempts: MaxAttempts), CancellationToken.None);

        clean.Should().Equal(KycApprovalLevel.Agent);
        struggling.Should().Equal(KycApprovalLevel.Agent, KycApprovalLevel.BranchManager);
    }

    [Fact]
    public async Task One_failed_comparison_is_not_two()
    {
        var levels = await Circuit().ResolveAsync(
            FileWith(KycVigilanceLevel.Low, MaxAttempts - 1), CancellationToken.None);

        levels.Should().Equal(KycApprovalLevel.Agent);
    }
}
