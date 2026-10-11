namespace Sankore.Modules.Kyc.Tests.Features.Approval;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Xunit;

/// <summary>
/// What the CIMA LBC/FT text requires before an institution enters into a relationship with a
/// POLITICALLY EXPOSED person: the general management's authorisation, and the origin of the
/// patrimony and funds established.
///
/// <para>
/// Both were missing. The compliance officer was the top of the ladder — which is a review, not an
/// authorisation — and nothing anywhere recorded where a client's money came from.
/// </para>
/// </summary>
public sealed class PepAuthorizationTests
{
    private const int MaxFaceAttempts = 2;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TimeProvider _clock = TimeProvider.System;
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();
    private readonly KycApprovalCircuit _circuit;

    public PepAuthorizationTests()
    {
        _settings.GetIntAsync(
                Arg.Any<Guid>(), KycSettingKeys.FaceMatchMaxAttempts, Arg.Any<CancellationToken>())
            .Returns(MaxFaceAttempts);

        _circuit = new KycApprovalCircuit(_settings);
    }

    private KycFile File(
        KycVigilanceLevel vigilance = KycVigilanceLevel.Standard, bool pep = false)
    {
        var file = KycFile.Open(
            _tenantId, Guid.NewGuid(), KycChannel.Agency, Guid.NewGuid(), _clock, vigilance);

        if (pep) file.MarkPoliticallyExposed(_clock);
        return file;
    }

    // ── the ladder ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_politically_exposed_file_ends_with_the_general_management()
    {
        // The text asks to « obtenir l'autorisation de la Direction générale avant d'entrer en
        // relation » — a decision above the compliance officer, not the same one renamed.
        var levels = await _circuit.ResolveAsync(
            File(KycVigilanceLevel.High, pep: true), CancellationToken.None);

        levels.Should().Equal(
            KycApprovalLevel.Agent,
            KycApprovalLevel.BranchManager,
            KycApprovalLevel.ComplianceOfficer,
            KycApprovalLevel.GeneralManagement);
    }

    [Fact]
    public async Task A_high_risk_file_that_is_NOT_a_PEP_stops_at_the_compliance_officer()
    {
        // The distinction the flag exists for. High vigilance is raised by several causes, and
        // putting management on every high-risk file would turn an authorisation into a formality.
        var levels = await _circuit.ResolveAsync(
            File(KycVigilanceLevel.High), CancellationToken.None);

        levels.Should().NotContain(KycApprovalLevel.GeneralManagement);
        levels.Should().Equal(
            KycApprovalLevel.Agent,
            KycApprovalLevel.BranchManager,
            KycApprovalLevel.ComplianceOfficer);
    }

    [Fact]
    public async Task The_authorisation_is_the_LAST_rung()
    {
        // Ordering is not cosmetic: the officer prepares the file, management decides whether the
        // institution takes the client. A ladder that asked management first would have them
        // authorise something nobody had reviewed.
        var levels = await _circuit.ResolveAsync(
            File(KycVigilanceLevel.High, pep: true), CancellationToken.None);

        levels[^1].Should().Be(KycApprovalLevel.GeneralManagement);
    }

    [Fact]
    public void The_PEP_marker_is_raised_and_never_cleared()
    {
        // Same posture as the vigilance level. Exposure lapsing is a decision M01 records on the
        // client; a KYC file already in a circuit must not quietly lose a rung under its
        // approvers.
        var file = File();
        file.IsPoliticallyExposed.Should().BeFalse();

        file.MarkPoliticallyExposed(_clock);
        file.IsPoliticallyExposed.Should().BeTrue();

        file.MarkPoliticallyExposed(_clock);
        file.IsPoliticallyExposed.Should().BeTrue("marking twice is idempotent, not a toggle");
    }

    // ── the origin of the funds ─────────────────────────────────────────────

    private static PepAuthorizationEvidence Evidence(ICustomersModule customers)
        => new(customers, NullLogger<PepAuthorizationEvidence>.Instance);

    private static ICustomersModule CustomersAnswering(bool recorded)
    {
        var customers = Substitute.For<ICustomersModule>();
        customers.HasRecordedFundsOriginAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(recorded);
        return customers;
    }

    [Fact]
    public async Task A_non_PEP_file_is_not_asked_about_the_origin_of_the_funds()
    {
        // Useful of anybody, required of a PEP. Demanding it everywhere would be the institution
        // inventing a rule — and the check does not even reach across modules.
        var customers = CustomersAnswering(recorded: false);

        var result = await Evidence(customers).EnsureSatisfiedAsync(File(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await customers.DidNotReceiveWithAnyArgs()
            .HasRecordedFundsOriginAsync(default, default, default);
    }

    [Fact]
    public async Task A_PEP_file_with_no_recorded_origin_cannot_be_validated()
    {
        var result = await Evidence(CustomersAnswering(recorded: false))
            .EnsureSatisfiedAsync(File(pep: true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FundsOriginRequired);
    }

    [Fact]
    public async Task A_PEP_file_with_a_recorded_origin_passes()
    {
        var result = await Evidence(CustomersAnswering(recorded: true))
            .EnsureSatisfiedAsync(File(pep: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_outage_reading_M01_REFUSES_the_validation()
    {
        // Fails closed. A cross-module read that cannot be made is not evidence that the origin
        // was established, and nothing is lost by making a validator retry — unlike a capture,
        // where losing a prospect is worse than not recognising a customer.
        var customers = Substitute.For<ICustomersModule>();
        customers.HasRecordedFundsOriginAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("M01 is down"));

        var result = await Evidence(customers)
            .EnsureSatisfiedAsync(File(pep: true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FundsOriginRequired);
    }

    [Fact]
    public async Task A_cancellation_propagates_rather_than_reading_as_a_refusal()
    {
        var customers = Substitute.For<ICustomersModule>();
        customers.HasRecordedFundsOriginAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await Evidence(customers)
            .EnsureSatisfiedAsync(File(pep: true), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
