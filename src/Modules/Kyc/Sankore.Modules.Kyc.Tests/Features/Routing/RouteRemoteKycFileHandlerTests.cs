namespace Sankore.Modules.Kyc.Tests.Features.Routing;

using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Features.Routing.Controls;
using Sankore.Modules.Kyc.Features.Routing.RouteRemoteFile;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Kyc.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// KRM-B-08's plumbing: the handler that gathers the facts, applies
/// <c>KycAutoApprovalPolicy</c>'s verdict, and must leave the file in exactly the state a human
/// circuit would have.
///
/// <para>
/// The conditions themselves are pinned case by case in <see cref="KycAutoApprovalPolicyTests"/>.
/// What is tested here is everything the policy cannot see: the end state of an automatic
/// approval, the events, the idempotence, the channel guard, and the fact that a manual route
/// moves no status.
/// </para>
/// </summary>
public sealed class RouteRemoteKycFileHandlerTests : IDisposable
{
    /// <summary>
    /// Sorti de l'appel pour satisfaire CA1861, que la configuration Release promeut en erreur.
    /// La règle vise un appel répété, ce qui n'est pas le cas ici — mais garder la famille à
    /// zéro est ce qui donne un sens au gate : il est là pour attraper une régression, et une
    /// violation permanente rend toutes les suivantes invisibles.
    /// </summary>
    private static readonly string[] LivenessPassedFlags = ["LIVENESS_PASSED"];

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly Guid _productLineId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();

    private readonly TestKycDbContextFactory _factory;
    private readonly KycDbContext _db;
    // TimeProvider.System, like every other test in this module: nothing here measures an
    // interval, and a fake clock would have to be threaded through RemoteSaleEvidence's
    // freshness window as well for no gain.
    private readonly TimeProvider _clock = TimeProvider.System;

    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IKycSettings _settings = Substitute.For<IKycSettings>();

    public RouteRemoteKycFileHandlerTests()
    {
        _factory = new TestKycDbContextFactory(_tenantId);
        _db = _factory.CreateContext();

        // The tenant permits machine approval, up to Simplified — the defaults a compliance
        // officer has to opt into.
        _administration.GetComplianceSettingsAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(Compliance(enabled: true, maxTier: "Simplified"));

        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy("Automatic"));

        _customers.MatchesReadIdentityAsync(
                _tenantId, _customerId, Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _settings.GetIntAsync(Arg.Any<Guid>(), KycSettingKeys.RemoteMaxQualityRetries, Arg.Any<CancellationToken>())
            .Returns(5);

        _sender.Send(Arg.Any<StartKycApprovalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Sankore.Shared.Kernel.Result.Ok(
                new StartKycApprovalResult(Guid.NewGuid(), ["Agent"], false, null)));
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private static ComplianceSettingsDto Compliance(bool enabled, string maxTier) => new(
        FatcaEnabled: false, FatcaRegime: null, FatcaGiin: null, FatcaRequiredAt: "Conversion",
        FatcaForCoHolders: false, PepEnabled: true, PepPostMandateMonths: 12,
        KycAutoApprovalEnabled: enabled, KycAutoApprovalMaxTier: maxTier,
        KycAutoApprovalSamplePct: 10);

    private KycRemotePolicyDto Policy(string mode, string tier = "Simplified") => new(
        ProductLineId: _productLineId,
        ProductCode: "EPARGNE-01",
        ProductName: "Épargne Sécurité",
        Category: "Savings",
        IsActive: true,
        Enabled: true,
        ValidationMode: mode,
        RequiredTier: tier,
        MinAutoScore: 85,
        RequiredDocuments: [],
        AllowedDocumentTypes: ["Cni"],
        LinkValidityHours: 72);

    private RouteRemoteKycFileHandler Handler() => new(
        _db, _sender, _administration, _customers,
        new KycRemoteJourney(_db, _administration, _settings),
        TestRemoteSaleEvidence.For(_db, _clock),
        new KycAutoApprovalSampler(_db, _clock, NullLogger<KycAutoApprovalSampler>.Instance),
        _publisher, _clock,
        NullLogger<RouteRemoteKycFileHandler>.Instance);

    /// <summary>
    /// A remotely submitted, scored file sitting in Validating with everything in order: the
    /// evidence rows, a proof of address an agent accepted, and an invitation marked Submitted.
    /// </summary>
    private async Task<KycFile> SeedApprovableAsync(int score = 92, bool proofOfAddress = true)
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, _agentId, _clock,
            agencyId: _agencyId, productLineId: _productLineId, responsibleAgentId: _agentId);

        // SYSTEM submits a remote file — that is what frees the responsible agent to decide it.
        file.SubmitForVerification(Guid.Empty, _clock);
        file.MarkRemotelyCollected(_clock);
        file.RecordFaceMatchAttempt(_clock);
        file.RecordVerification(score, KycConfidenceLevel.High, _clock);

        _db.KycFiles.Add(file);

        _db.KycConfidenceAssessments.Add(KycConfidenceAssessment.Create(
            _tenantId, file.Id, score, KycConfidenceLevel.High,
            trigger: "VERIFICATION", clock: _clock,
            flagsJson: JsonSerializer.Serialize(LivenessPassedFlags)));

        _db.KycFaceVerifications.Add(KycFaceVerification.Create(
            _tenantId, file.Id, attempt: 1, similarityScore: 0.94, isMatch: true, clock: _clock,
            selfieStorageRef: "kyc/selfie"));

        _db.KycIdentityDocuments.Add(KycIdentityDocument.Create(
            tenantId: _tenantId,
            kycFileId: file.Id,
            docType: "Cni",
            encryptedNumber: "enc",
            numberBlindIndex: "idx",
            clock: _clock,
            ocrFieldsJson: JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["surname"] = "OUATTARA",
                ["given_names"] = "AWA",
                ["date_of_birth"] = "1987-04-02",
            })));

        var invitation = KycRemoteInvitation.Issue(
            _tenantId, file.Id, _customerId, validityHours: 72, createdBy: Guid.Empty, clock: _clock);
        invitation.AttachTokenAndMarkSent(new byte[32], _clock);
        invitation.MarkIdentified(_clock);
        invitation.MarkSubmitted(_clock);
        _db.KycRemoteInvitations.Add(invitation);

        _db.KycDocuments.Add(KycDocument.Register(
            _tenantId, file.Id, KycDocumentKind.IdentityDocumentFront, "kyc/front",
            "image/jpeg", 1024, "sha", Guid.Empty, _clock,
            declaredDocumentType: KycDocumentType.Passport));

        _db.KycDocuments.Add(KycDocument.Register(
            _tenantId, file.Id, KycDocumentKind.Selfie, "kyc/selfie",
            "image/jpeg", 1024, "sha", Guid.Empty, _clock));

        if (proofOfAddress)
        {
            // Accepted AND dated: RemoteSaleEvidence requires both, and a remote file cannot be
            // approved — by a human or by the policy — without them.
            var proof = KycDocument.Register(
                _tenantId, file.Id, KycDocumentKind.ProofOfAddress, "kyc/proof",
                "image/jpeg", 2048, "sha", Guid.Empty, _clock,
                documentIssuedOn: DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-10));

            proof.Review(KycDocumentReviewDecision.Accepted, Guid.NewGuid(), _clock);
            _db.KycDocuments.Add(proof);
        }

        await _db.SaveChangesAsync(CancellationToken.None);
        return file;
    }

    private Task<Sankore.Shared.Kernel.Result<RouteRemoteKycFileResult>> RouteAsync(Guid fileId)
        => Handler().Handle(
            new RouteRemoteKycFileCommand(_tenantId, fileId, _clock.GetUtcNow()),
            CancellationToken.None);

    private Task<KycFile> ReloadAsync(Guid id)
        => _db.KycFiles.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(f => f.Id == id, CancellationToken.None);

    // ── The automatic approval ──────────────────────────────────────────────

    [Fact]
    public async Task A_clean_file_reaches_exactly_the_state_a_human_circuit_would_have_left_it_in()
    {
        var file = await SeedApprovableAsync();

        var result = await RouteAsync(file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Automatic.Should().BeTrue();

        var reloaded = await ReloadAsync(file.Id);

        // The same four facts DecideKycApprovalHandler's final rung produces: the status derived
        // from the tier, the tier itself, a validation timestamp, and no routing reason. If any of
        // these drifted, M01 would activate a client off an event M02 disagrees with.
        reloaded.Status.Should().Be(KycFileStatus.Simplified);
        reloaded.Tier.Should().Be(KycTier.Simplified);
        reloaded.ValidatedAt.Should().NotBeNull();
        reloaded.ValidationRoute.Should().Be(KycValidationRoute.Automatic);
        reloaded.RoutingReason.Should().BeNull();
        reloaded.RoutedAt.Should().Be(reloaded.ValidatedAt);

        // And no review scheduled — because the MANUAL path does not schedule one either. Matching
        // it is the requirement; an automatic path that scheduled a review would be a divergence
        // in the other direction, and the gap is M02's to close for both paths at once.
        reloaded.NextReviewDate.Should().BeNull();
    }

    [Fact]
    public async Task It_publishes_the_SAME_two_events_the_last_rung_of_a_human_circuit_publishes()
    {
        var file = await SeedApprovableAsync();

        await RouteAsync(file.Id);

        // KycValidated is what M01 activates the client on; KycTierChanged is what M03 and M07
        // refresh their ceilings on. A third event meaning "validated automatically" would be a
        // second definition of validated, which is exactly how two modules come to disagree about
        // whether somebody is a customer.
        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycValidatedEvent>(e =>
                e.TenantId == _tenantId && e.CustomerEntityId == _customerId),
            Arg.Any<CancellationToken>());

        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycTierChangedEvent>(e =>
                e.PreviousTier == "None" && e.CurrentTier == "Simplified"),
            Arg.Any<CancellationToken>());

        // Plus the one that says NOBODY decided — which the two above cannot say, and which three
        // unrelated consumers need.
        await _publisher.Received(1).PublishAsync(
            Arg.Is<KycRemoteDecidedEvent>(e => e.Automatic && e.RoutingReason == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_decision_is_recorded_as_an_Automatic_step_signed_by_SYSTEM()
    {
        var file = await SeedApprovableAsync();

        await RouteAsync(file.Id);

        var step = await _db.KycApprovalSteps
            .IgnoreQueryFilters()
            .SingleAsync(s => s.KycFileId == file.Id, CancellationToken.None);

        step.Level.Should().Be(KycApprovalLevel.Automatic);
        step.Decision.Should().Be(KycApprovalDecision.Approved);
        step.ApproverId.Should().Be(Guid.Empty);
        step.Comment.Should().StartWith("Approbation automatique — politique produit");

        // The rules that were evaluated, as KRM-B-08 asks. No score and no flag: the comment is
        // shown on the file's history, and a score printed beside an approval reads as the reason
        // it was given.
        step.Comment.Should().Contain(KycRoutingReasons.ScoreBelowThreshold);
        step.Comment.Should().NotContain("92");
    }

    [Fact]
    public async Task The_Automatic_step_is_the_ONLY_step_the_file_carries()
    {
        // The circuit is not written first and then unwound: deleting approval steps is the one
        // thing a circuit must never do, so RunKycVerificationHandler skips it for a remote file
        // and the router writes either this step or the human ladder.
        var file = await SeedApprovableAsync();

        await RouteAsync(file.Id);

        var steps = await _db.KycApprovalSteps
            .IgnoreQueryFilters()
            .Where(s => s.KycFileId == file.Id)
            .ToListAsync(CancellationToken.None);

        steps.Should().HaveCount(1);
        await _sender.DidNotReceive().Send(
            Arg.Any<StartKycApprovalCommand>(), Arg.Any<CancellationToken>());
    }

    // ── The manual route ────────────────────────────────────────────────────

    [Fact]
    public async Task A_manual_product_routes_to_a_human_and_moves_no_status()
    {
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy("Manual"));

        var file = await SeedApprovableAsync();

        var result = await RouteAsync(file.Id);

        result.Value.Automatic.Should().BeFalse();
        result.Value.Reason.Should().Be(KycRoutingReasons.ProductPolicy);

        var reloaded = await ReloadAsync(file.Id);

        // Routing says WHO decides. The file stays where the verification left it, and nothing
        // about a tier has happened.
        reloaded.Status.Should().Be(KycFileStatus.Validating);
        reloaded.Tier.Should().Be(KycTier.None);
        reloaded.ValidatedAt.Should().BeNull();
        reloaded.ValidationRoute.Should().Be(KycValidationRoute.Manual);
        reloaded.RoutingReason.Should().Be(KycRoutingReasons.ProductPolicy);
        reloaded.RoutedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_manual_route_opens_the_human_circuit_it_did_not_open_before()
    {
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy("Manual"));

        var file = await SeedApprovableAsync();

        await RouteAsync(file.Id);

        await _sender.Received(1).Send(
            Arg.Is<StartKycApprovalCommand>(c => c.KycFileId == file.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_sent_back_for_a_complement_is_routed_but_gets_no_circuit()
    {
        // A refused score leaves the file in ComplementRequired. There is nothing to approve yet,
        // so a circuit would show rungs for a decision nobody will be asked for — the agent's next
        // act is a complement or a refusal, each with its own slice.
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy("Manual"));

        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.LeadConversion, _agentId, _clock,
            agencyId: _agencyId, productLineId: _productLineId, responsibleAgentId: _agentId);

        file.SubmitForVerification(Guid.Empty, _clock);
        file.MarkRemotelyCollected(_clock);
        file.RecordVerification(12, KycConfidenceLevel.Rejected, _clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync(CancellationToken.None);

        var result = await RouteAsync(file.Id);

        result.Value.Automatic.Should().BeFalse();
        (await ReloadAsync(file.Id)).Status.Should().Be(KycFileStatus.ComplementRequired);

        await _sender.DidNotReceive().Send(
            Arg.Any<StartKycApprovalCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_remote_file_with_no_accepted_proof_of_address_is_NOT_approved()
    {
        // The property that stops the automatic path from being weaker than the manual one:
        // RemoteSaleEvidence refuses the same file at the final rung of a human circuit.
        var file = await SeedApprovableAsync(proofOfAddress: false);

        var result = await RouteAsync(file.Id);

        result.Value.Automatic.Should().BeFalse();
        result.Value.Reason.Should().Be(KycRoutingReasons.ProofOfAddressRequired);
        (await ReloadAsync(file.Id)).Tier.Should().Be(KycTier.None);
    }

    // ── Guards and idempotence ──────────────────────────────────────────────

    [Fact]
    public async Task A_file_collected_at_a_counter_is_left_completely_alone()
    {
        var file = KycFile.Open(
            _tenantId, _customerId, KycChannel.Agency, _agentId, _clock, agencyId: _agencyId);

        file.SubmitForVerification(_agentId, _clock);
        file.RecordVerification(98, KycConfidenceLevel.High, _clock);

        _db.KycFiles.Add(file);
        await _db.SaveChangesAsync(CancellationToken.None);

        var result = await RouteAsync(file.Id);

        result.Value.Routed.Should().BeFalse();

        var reloaded = await ReloadAsync(file.Id);
        reloaded.ValidationRoute.Should().BeNull();
        reloaded.Status.Should().Be(KycFileStatus.Validating);
    }

    [Fact]
    public async Task A_redelivered_event_does_not_re_evaluate_an_already_routed_file()
    {
        // KRM-B-08's "décision idempotente : une seule évaluation par vérification". The broker
        // redelivers, and the second evaluation must not overwrite the reason an agent is reading
        // — nor, far worse, approve a file somebody has since sent back.
        _administration.GetKycRemotePolicyAsync(_tenantId, _productLineId, Arg.Any<CancellationToken>())
            .Returns(Policy("Manual"));

        var file = await SeedApprovableAsync();

        await RouteAsync(file.Id);
        _publisher.ClearReceivedCalls();

        var second = await RouteAsync(file.Id);

        second.Value.Routed.Should().BeFalse();
        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<KycRemoteDecidedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_redelivered_event_cannot_approve_a_file_twice()
    {
        var file = await SeedApprovableAsync();

        await RouteAsync(file.Id);
        _publisher.ClearReceivedCalls();

        var second = await RouteAsync(file.Id);

        second.Value.Routed.Should().BeFalse();

        var steps = await _db.KycApprovalSteps
            .IgnoreQueryFilters()
            .Where(s => s.KycFileId == file.Id)
            .CountAsync(CancellationToken.None);

        steps.Should().Be(1);
        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<KycValidatedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_of_another_tenant_is_simply_absent()
    {
        var file = await SeedApprovableAsync();

        var result = await Handler().Handle(
            new RouteRemoteKycFileCommand(Guid.NewGuid(), file.Id, _clock.GetUtcNow()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(KycErrors.FileNotFound);
        (await ReloadAsync(file.Id)).ValidationRoute.Should().BeNull();
    }

    [Fact]
    public async Task An_M12_outage_reading_the_tenant_switch_does_NOT_approve()
    {
        // GetComplianceSettingsAsync never returns null — a tenant with no row runs on the platform
        // defaults, whose KycAutoApprovalEnabled is false. This pins the direction: an outage, or a
        // tenant that never configured anything, asks for a human.
        _administration.GetComplianceSettingsAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(Compliance(enabled: false, maxTier: "Simplified"));

        var file = await SeedApprovableAsync();

        var result = await RouteAsync(file.Id);

        result.Value.Automatic.Should().BeFalse();
        result.Value.Reason.Should().Be(KycRoutingReasons.AutoApprovalDisabled);
    }

    [Fact]
    public async Task An_identity_M01_will_not_confirm_routes_to_a_human()
    {
        _customers.MatchesReadIdentityAsync(
                _tenantId, _customerId, Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var file = await SeedApprovableAsync();

        var result = await RouteAsync(file.Id);

        result.Value.Reason.Should().Be(KycRoutingReasons.IdentityMismatch);
    }

    [Fact]
    public async Task The_OCR_reading_is_what_is_sent_to_M01_and_nothing_else_is()
    {
        var file = await SeedApprovableAsync();

        await RouteAsync(file.Id);

        // Field names are the service's own, matched through KycOcrFields — the same code the
        // verification used to WRITE the projection. Two private copies of that matching is how a
        // service rename silently makes every remote file fail its identity check.
        await _customers.Received(1).MatchesReadIdentityAsync(
            _tenantId, _customerId, "OUATTARA", "AWA",
            new DateOnly(1987, 4, 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_whose_ocr_projection_is_unreadable_routes_to_a_human()
    {
        // Our own column that we cannot read back is an incident, not a customer's fault — and the
        // gate must not throw, which would leave the file in validation with nobody told.
        var file = await SeedApprovableAsync();

        var document = await _db.KycIdentityDocuments
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstAsync(d => d.KycFileId == file.Id, CancellationToken.None);

        // ReplaceOcrFields is the aggregate's own setter — the field-correction slice uses it,
        // so no reflection is needed to produce a payload nothing can parse.
        document.ReplaceOcrFields("{not json");

        await _db.SaveChangesAsync(CancellationToken.None);

        var result = await RouteAsync(file.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value.Reason.Should().Be(KycRoutingReasons.IdentityMismatch);
    }
}
