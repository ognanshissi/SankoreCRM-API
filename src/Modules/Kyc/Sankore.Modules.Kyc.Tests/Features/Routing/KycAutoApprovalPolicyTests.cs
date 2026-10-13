namespace Sankore.Modules.Kyc.Tests.Features.Routing;

using FluentAssertions;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Routing;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Xunit;

/// <summary>
/// KRM-B-08 §3.2, condition by condition — which is what the specification's definition of done
/// asks for in those words ("règles d'approbation automatique couvertes par des tests unitaires
/// cas par cas").
///
/// <para>
/// Each case starts from a file that WOULD be approved and breaks exactly one thing, so a failure
/// names the condition rather than "the policy said no". The all-clear case is first and is the
/// one that keeps the rest honest: a mistake that made the policy refuse everything would turn
/// every negative case green.
/// </para>
/// </summary>
public sealed class KycAutoApprovalPolicyTests
{
    /// <summary>
    /// A clean, approvable file: automatic mode, the tenant allows it, a high score on the first
    /// face match, an identity that agrees, no flag, nothing missing, and a proof of address the
    /// branch has accepted.
    /// </summary>
    private static KycAutoApprovalPolicy.Input Clean() => new(
        TenantAllowsAutoApproval: true,
        TenantMaxTier: "Simplified",
        PolicyEnabled: true,
        PolicyIsAutomatic: true,
        RequiredTier: "Simplified",
        MinAutoScore: 85,
        ConfidenceLevel: KycConfidenceLevel.High,
        ConfidenceScore: 92,
        Flags: ["MRZ_OK", "LIVENESS_PASSED"],
        IdentityMatchesRecord: true,
        IsPoliticallyExposed: false,
        VigilanceLevel: KycVigilanceLevel.Low,
        DuplicateSuspected: false,
        FaceMatchedFirstTry: true,
        HasOpenEventReview: false,
        MissingDocumentKinds: [],
        DocumentsOverRetryBudget: [],
        RemoteSaleEvidenceSatisfied: true);

    [Fact]
    public void A_clean_file_is_approved_automatically_at_the_product_tier()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(Clean());

        decision.Automatic.Should().BeTrue();
        decision.Reason.Should().BeNull();
        decision.Tier.Should().Be(KycTier.Simplified);

        // Every rule is reported, not just the failing one: the stored verdict has to show a
        // compliance officer what was checked, which is the whole value of keeping it.
        decision.Rules.Should().OnlyContain(r => r.Passed);
        decision.Rules.Should().HaveCountGreaterThan(8);
    }

    // ── Configuration ───────────────────────────────────────────────────────

    [Fact]
    public void A_product_in_manual_mode_routes_to_a_human_with_PRODUCT_POLICY()
    {
        // The specification's "Mode Manual du produit : routage manuel direct, motif
        // PRODUCT_POLICY". It is also the DEFAULT of KycRemotePolicy, so this is the behaviour
        // every product has until somebody deliberately changes it.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with { PolicyIsAutomatic = false });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.ProductPolicy);
    }

    [Fact]
    public void A_product_whose_remote_collection_was_disabled_mid_flight_also_says_PRODUCT_POLICY()
    {
        // An administrator switched remote collection off while a customer was uploading. The file
        // still has to reach a decision, and a human takes it.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with { PolicyEnabled = false });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.ProductPolicy);
    }

    [Fact]
    public void The_tenant_switch_wins_over_the_product_and_says_so_in_its_own_code()
    {
        // KRM-B-01 refuses to SAVE a product in automatic mode while the tenant switch is off, so
        // this state is only reachable by switching it off afterwards — which a compliance officer
        // is entitled to do and needs to see take effect. The reason is its own code and not
        // TIER_ABOVE_AUTO_LIMIT, which would send them to edit the wrong screen.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { TenantAllowsAutoApproval = false });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.AutoApprovalDisabled);
    }

    [Fact]
    public void A_product_asking_for_a_tier_above_the_tenant_ceiling_is_refused()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { RequiredTier = "Full", TenantMaxTier = "Simplified" });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.TierAboveAutoLimit);
    }

    [Fact]
    public void A_ceiling_of_Full_admits_a_Simplified_product()
    {
        // Compared on the enum's ORDER, not by equality: a ceiling is a maximum.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with { TenantMaxTier = "Full" });

        decision.Automatic.Should().BeTrue();
        decision.Tier.Should().Be(KycTier.Simplified);
    }

    [Fact]
    public void A_full_tier_product_is_approved_when_the_tenant_allows_it()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { RequiredTier = "Full", TenantMaxTier = "Full" });

        decision.Automatic.Should().BeTrue();
        decision.Tier.Should().Be(KycTier.Full);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("Platinum")]
    public void An_unreadable_tenant_ceiling_fails_CLOSED(string? ceiling)
    {
        // The one value a corrupted compliance setting must never mean is "no limit".
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with { TenantMaxTier = ceiling });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.TierAboveAutoLimit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    [InlineData("Gold")]
    public void An_unreadable_product_tier_fails_CLOSED_too(string? tier)
    {
        // KycTier.None is not a tier that can be granted, so an unconfigured product cannot be
        // approved by accident — and the decision carries None rather than a guess.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with { RequiredTier = tier });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.TierAboveAutoLimit);
        decision.Tier.Should().Be(KycTier.None);
    }

    // ── Evidence ────────────────────────────────────────────────────────────

    [Fact]
    public void A_score_below_the_products_floor_routes_to_a_human()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { ConfidenceScore = 84, MinAutoScore = 85 });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.ScoreBelowThreshold);
    }

    [Fact]
    public void The_floor_is_inclusive()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { ConfidenceScore = 85, MinAutoScore = 85 });

        decision.Automatic.Should().BeTrue();
    }

    [Theory]
    [InlineData(KycConfidenceLevel.Medium)]
    [InlineData(KycConfidenceLevel.Low)]
    [InlineData(KycConfidenceLevel.Rejected)]
    public void Only_the_HIGH_confidence_level_can_be_approved(KycConfidenceLevel level)
    {
        // §3.2 says "Niveau de confiance Validated", and there is no such level — see the policy's
        // own remarks. It is read as High, the strictest grade anything produces, AND the score
        // floor still applies on top. This case is what stops that reading from quietly loosening
        // into "anything but Rejected": a Medium file with a 99 score is still a human's decision.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { ConfidenceLevel = level, ConfidenceScore = 99 });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.ScoreBelowThreshold);
    }

    [Fact]
    public void A_refused_score_is_routed_and_never_rejected()
    {
        // "Le système ne rejette jamais ; un score Rejected part en manuel, l'agent décide du
        // complément ou du rejet." The policy has no verdict that refuses a customer at all —
        // this case pins that the worst possible evidence still produces "ask a human".
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with
        {
            ConfidenceLevel = KycConfidenceLevel.Rejected,
            ConfidenceScore = 3,
            Flags = ["FACE_MISMATCH", "DOCUMENT_EXPIRED"],
            IdentityMatchesRecord = false,
            FaceMatchedFirstTry = false,
        });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().NotBeNull();
    }

    [Fact]
    public void A_file_that_was_never_scored_cannot_be_approved()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { ConfidenceLevel = null, ConfidenceScore = null });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.ScoreBelowThreshold);
    }

    [Theory]
    [InlineData("MRZ_CHECKSUM_FAILED")]
    [InlineData("VISUAL_MRZ_MISMATCH")]
    [InlineData("DOCUMENT_EXPIRED")]
    [InlineData("FACE_MISMATCH")]
    [InlineData("DUPLICATE_DOCUMENT_SUSPECTED")]
    public void Each_of_the_five_blocking_flags_routes_to_a_human(string flag)
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { Flags = ["LIVENESS_PASSED", flag] });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.BlockingFlag);
    }

    [Fact]
    public void A_blocking_flag_is_matched_whatever_case_the_service_sent_it_in()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { Flags = ["  face_mismatch  "] });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.BlockingFlag);
    }

    [Fact]
    public void A_flag_the_list_does_not_know_is_NOT_blocking()
    {
        // The list is the compliance decision. A service deployment that starts emitting a new
        // flag must not silently start routing an institution's whole remote intake to humans,
        // and must not silently start blocking on something nobody assessed either.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { Flags = ["SOME_FUTURE_SERVICE_FLAG", "GLARE_DETECTED"] });

        decision.Automatic.Should().BeTrue();
    }

    [Fact]
    public void The_files_own_duplicate_suspicion_blocks_even_when_the_scorer_said_nothing()
    {
        // KycDuplicateDetection sets this from a document-number collision, which the scorer never
        // sees. §3.2's blocking list names DUPLICATE_DOCUMENT_SUSPECTED, so the flag is folded into
        // the same condition rather than given a code of its own.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { DuplicateSuspected = true });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.BlockingFlag);
    }

    [Fact]
    public void An_identity_that_does_not_agree_with_the_record_routes_to_a_human()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { IdentityMatchesRecord = false });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.IdentityMismatch);
    }

    [Fact]
    public void A_politically_exposed_person_is_never_approved_by_a_machine()
    {
        // The CIMA text asks for the general management's authorisation before the relationship is
        // entered into. That is a decision above a compliance officer, let alone above a model.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { IsPoliticallyExposed = true });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.Pep);
    }

    [Fact]
    public void High_vigilance_routes_to_a_human()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { VigilanceLevel = KycVigilanceLevel.High });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.HighVigilance);
    }

    [Fact]
    public void Standard_vigilance_does_not()
    {
        // §3.2 says "vigilance faible ou standard", so Standard must pass. Pinned because the
        // obvious misreading — comparing against Low — would quietly exclude most of a tenant's
        // portfolio and look like the feature simply not working.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { VigilanceLevel = KycVigilanceLevel.Standard });

        decision.Automatic.Should().BeTrue();
    }

    [Fact]
    public void An_open_event_driven_review_is_this_platforms_LBC_FT_alert()
    {
        // §3.2's "aucune alerte LBC-FT ouverte". There is no AML-alert entity here, and an open
        // event-driven KycReviewSchedule is the nearest true reading — see the policy's remarks.
        // It shares HIGH_VIGILANCE's code because it says the same thing to the agent: this file
        // is being watched.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with { HasOpenEventReview = true });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.HighVigilance);
    }

    [Fact]
    public void A_face_match_that_needed_a_second_attempt_routes_to_a_human()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { FaceMatchedFirstTry = false });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.FaceRetry);
    }

    [Fact]
    public void A_missing_required_document_routes_to_a_human()
    {
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with
        {
            MissingDocumentKinds = [KycDocumentKind.ProofOfAddress],
        });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.MissingDocument);
    }

    [Fact]
    public void A_customer_who_exhausted_their_photo_budget_gets_a_human()
    {
        // KRM-B-06 lets the upload through past the budget rather than stranding somebody whose
        // camera the service dislikes. This is the other half of that bargain: what was finally
        // sent gets looked at.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with
        {
            DocumentsOverRetryBudget = ["IdentityDocumentFront"],
        });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.QualityRetriesExceeded);
    }

    [Fact]
    public void A_remote_file_with_no_accepted_proof_of_address_routes_to_a_human()
    {
        // NOT in §3.2, and it has to be here: RemoteSaleEvidence refuses the final approval of any
        // remote file without one, so an automatic approval that skipped it would grant a tier the
        // manual path would have refused on the very same file. That is the one way this feature
        // could be worse than no feature.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { RemoteSaleEvidenceSatisfied = false });

        decision.Automatic.Should().BeFalse();
        decision.Reason.Should().Be(KycRoutingReasons.ProofOfAddressRequired);
    }

    // ── Precedence and reporting ────────────────────────────────────────────

    [Fact]
    public void Configuration_is_reported_before_evidence()
    {
        // A file that fails everything reports the PRODUCT, because an officer who left a product
        // in manual mode must not be sent to examine a score that was never going to be read.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with
        {
            PolicyIsAutomatic = false,
            TenantAllowsAutoApproval = false,
            ConfidenceScore = 1,
            IdentityMatchesRecord = false,
        });

        decision.Reason.Should().Be(KycRoutingReasons.ProductPolicy);
    }

    [Fact]
    public void The_proof_of_address_is_reported_LAST_so_it_never_masks_a_bad_score()
    {
        // It is the condition most likely to fail on a correctly configured tenant, and the reason
        // is first-failure. Ahead of the evidence rules it would hide every score problem behind a
        // missing utility bill, and nobody tuning the feature would ever see what discriminates.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean() with
        {
            RemoteSaleEvidenceSatisfied = false,
            ConfidenceScore = 10,
        });

        decision.Reason.Should().Be(KycRoutingReasons.ScoreBelowThreshold);
    }

    [Fact]
    public void Every_rule_is_evaluated_even_once_one_has_failed()
    {
        // The stored verdict is meant to show a compliance officer what was checked on a file that
        // was routed out. A short-circuit would make it show only the first thing that went wrong.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { PolicyIsAutomatic = false, IsPoliticallyExposed = true });

        decision.Rules.Should().Contain(r => r.Rule == KycRoutingReasons.Pep && !r.Passed);
        decision.Rules.Should().Contain(
            r => r.Rule == KycRoutingReasons.ScoreBelowThreshold && r.Passed);
    }

    [Fact]
    public void Every_reason_code_is_owned_by_exactly_one_rule()
    {
        // Two rules sharing a code would make a routing reason ambiguous — an agent's screen would
        // translate it one way and a compliance count would mean another. HIGH_VIGILANCE covers
        // two FACTS deliberately (the level and an open review) but it is still one rule.
        var decision = KycAutoApprovalPolicy.Evaluate(Clean());

        decision.Rules.Select(r => r.Rule).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_failing_rules_detail_never_carries_an_identity()
    {
        // §8: no identity data in logs or stored payloads. The details are stored on the automatic
        // decision and logged on a manual route, so they may name a flag, a score or a count —
        // never a name, a number or a date read on the document.
        var decision = KycAutoApprovalPolicy.Evaluate(
            Clean() with { IdentityMatchesRecord = false });

        var detail = decision.Rules
            .Single(r => r.Rule == KycRoutingReasons.IdentityMismatch)
            .Detail;

        detail.Should().NotBeNull();
        detail.Should().NotContainAny("1987", "OUATTARA", "AWA");
    }
}
