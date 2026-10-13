namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Covers <see cref="KycRemotePolicy"/> (KRM-B-01): what an unconfigured line carries, what the
/// closed lists refuse, and the two refusals that depend on the TENANT rather than on the product.
///
/// <para>
/// The tenant-dependent pair is the point of this feature and the reason these checks live in the
/// value object rather than in the validator: the product screen must be unable to save
/// "Automatic" on an institution that has not adopted machine approval, because a configuration
/// that saves and then behaves manually is the one discrepancy an administrator cannot debug.
/// </para>
/// </summary>
public sealed class KycRemotePolicyTests
{
    private static Result<KycRemotePolicy> Create(
        bool enabled = true,
        string? mode = KycRemotePolicyValues.ManualMode,
        string? tier = null,
        int minScore = KycRemotePolicyValues.DefaultMinAutoScore,
        IReadOnlyList<string>? documents = null,
        IReadOnlyList<string>? types = null,
        int linkHours = KycRemotePolicyValues.DefaultLinkValidityHours,
        bool tenantAllowsAuto = false,
        string tenantMaxTier = KycRemotePolicyValues.SimplifiedTier)
        => KycRemotePolicy.Create(
            enabled, mode, tier, minScore, documents, types ?? ["Cni"], linkHours,
            tenantAllowsAuto, tenantMaxTier);

    // ── The unconfigured default ──────────────────────────────────────────

    [Fact]
    public void Disabled_sends_no_link_and_validates_by_hand()
    {
        var none = KycRemotePolicy.Disabled;

        none.Enabled.Should().BeFalse();
        none.ValidationMode.Should().Be(KycRemotePolicyValues.ManualMode);
        none.WantsAutomaticValidation.Should().BeFalse();
        none.Documents.Should().BeEmpty();
        none.DocumentTypes.Should().BeEmpty();
    }

    [Fact]
    public void Disabled_still_carries_the_platform_score_floor_and_link_lifetime()
    {
        // Not cosmetic. `'{}'::jsonb` is the column default, so this record is what every existing
        // product row reads back as — and a zero score floor on a line somebody later switches to
        // Automatic would approve every answer the scoring service produced.
        KycRemotePolicy.Disabled.MinAutoScore
            .Should().Be(KycRemotePolicyValues.DefaultMinAutoScore);
        KycRemotePolicy.Disabled.LinkValidityHours
            .Should().Be(KycRemotePolicyValues.DefaultLinkValidityHours);
    }

    // ── Normalisation ─────────────────────────────────────────────────────

    [Fact]
    public void Values_are_stored_in_their_canonical_spelling()
    {
        var policy = Create(
            mode: "automatic", tier: "full", types: ["cni", " PASSPORT "],
            documents: ["proofofaddress"],
            tenantAllowsAuto: true, tenantMaxTier: KycRemotePolicyValues.FullTier);

        policy.IsSuccess.Should().BeTrue();
        policy.Value.ValidationMode.Should().Be("Automatic");
        policy.Value.RequiredTier.Should().Be("Full");
        policy.Value.DocumentTypes.Should().Equal("Cni", "Passport");
        policy.Value.Documents.Should().Equal("ProofOfAddress");
    }

    [Fact]
    public void A_blank_mode_reads_as_manual()
    {
        // The absence of a choice is the conservative choice, not an error: a client that omits
        // the field gets a human in the circuit.
        Create(mode: null).Value.ValidationMode.Should().Be(KycRemotePolicyValues.ManualMode);
        Create(mode: "  ").Value.ValidationMode.Should().Be(KycRemotePolicyValues.ManualMode);
    }

    [Fact]
    public void Repeated_values_are_collapsed()
    {
        var policy = Create(types: ["Cni", "cni"], documents: ["Selfie", "selfie"]);

        policy.Value.DocumentTypes.Should().Equal("Cni");
        policy.Value.Documents.Should().Equal("Selfie");
    }

    // ── The closed lists ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Sometimes")]
    [InlineData("auto")]
    public void An_unknown_validation_mode_is_refused(string mode)
        => Create(mode: mode).Error.Should().Be("KYC_REMOTE_VALIDATION_MODE_INVALID");

    [Fact]
    public void An_unknown_tier_is_refused()
        => Create(tier: "Tier1").Error.Should().Be("KYC_REMOTE_TIER_INVALID");

    [Fact]
    public void An_unknown_document_kind_names_the_offending_value()
    {
        // Named rather than ignored, the same choice BeneficiaryPolicy makes for a required field:
        // a silently dropped kind is a document the customer is never asked for, and nobody can
        // see which one it was.
        Create(documents: ["Payslip"]).Error
            .Should().Be("KYC_REMOTE_DOCUMENT_KIND_INVALID:Payslip");
    }

    [Fact]
    public void An_unknown_document_type_names_the_offending_value()
        => Create(types: ["DriverLicence"]).Error
            .Should().Be("KYC_REMOTE_DOCUMENT_TYPE_INVALID:DriverLicence");

    [Fact]
    public void An_enabled_line_must_accept_at_least_one_identity_document()
        => Create(enabled: true, types: []).Error
            .Should().Be("KYC_REMOTE_DOCUMENT_TYPES_REQUIRED");

    [Fact]
    public void A_disabled_line_needs_no_accepted_document()
    {
        // Otherwise an administrator would have to declare an acceptance in order to switch the
        // feature off.
        Create(enabled: false, types: []).IsSuccess.Should().BeTrue();
    }

    // ── Ranges ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(101)]
    public void A_score_floor_outside_50_to_100_is_refused(int score)
        => Create(minScore: score).Error.Should().Be("KYC_REMOTE_MIN_SCORE_INVALID");

    [Fact]
    public void The_score_floor_is_validated_even_in_manual_mode()
    {
        // The value stored now is the one a switch to Automatic will use; accepting nonsense while
        // Manual only defers the refusal to a moment when nobody remembers typing it.
        Create(mode: KycRemotePolicyValues.ManualMode, minScore: 10)
            .Error.Should().Be("KYC_REMOTE_MIN_SCORE_INVALID");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(337)]
    public void A_link_lifetime_outside_1_to_336_hours_is_refused(int hours)
        => Create(linkHours: hours).Error.Should().Be("KYC_REMOTE_LINK_VALIDITY_INVALID");

    // ── The two tenant-dependent refusals ─────────────────────────────────

    [Fact]
    public void Automatic_is_refused_when_the_tenant_has_not_enabled_machine_approval()
        => Create(mode: KycRemotePolicyValues.AutomaticMode, tenantAllowsAuto: false)
            .Error.Should().Be("KYC_REMOTE_AUTO_APPROVAL_DISABLED");

    [Fact]
    public void Automatic_is_refused_when_the_required_tier_exceeds_the_tenant_ceiling()
        => Create(
                mode: KycRemotePolicyValues.AutomaticMode,
                tier: KycRemotePolicyValues.FullTier,
                tenantAllowsAuto: true,
                tenantMaxTier: KycRemotePolicyValues.SimplifiedTier)
            .Error.Should().Be("KYC_REMOTE_TIER_ABOVE_AUTO_LIMIT");

    [Fact]
    public void Automatic_is_accepted_at_the_ceiling_itself()
        => Create(
                mode: KycRemotePolicyValues.AutomaticMode,
                tier: KycRemotePolicyValues.SimplifiedTier,
                tenantAllowsAuto: true,
                tenantMaxTier: KycRemotePolicyValues.SimplifiedTier)
            .IsSuccess.Should().BeTrue();

    [Fact]
    public void Automatic_with_no_tier_of_its_own_is_not_measured_against_the_ceiling()
    {
        // A line that states no tier grants the tenant's baseline, which is the ceiling's own
        // concern. Refusing it here would make "Automatic" unreachable for every line that leaves
        // the level to the institution.
        Create(
                mode: KycRemotePolicyValues.AutomaticMode,
                tier: null,
                tenantAllowsAuto: true,
                tenantMaxTier: KycRemotePolicyValues.SimplifiedTier)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Manual_is_never_measured_against_the_tenant_ceiling()
    {
        // A line may perfectly well require the FULL tier and have a human grant it; the ceiling
        // is about what the MACHINE may decide, not about what the product may ask for.
        Create(
                mode: KycRemotePolicyValues.ManualMode,
                tier: KycRemotePolicyValues.FullTier,
                tenantAllowsAuto: false,
                tenantMaxTier: KycRemotePolicyValues.SimplifiedTier)
            .IsSuccess.Should().BeTrue();
    }

    // ── The ceiling comparison itself ─────────────────────────────────────

    [Theory]
    [InlineData("Simplified", "Simplified", true)]
    [InlineData("Simplified", "Full", true)]
    [InlineData("Full", "Full", true)]
    [InlineData("Full", "Simplified", false)]
    public void Tier_ordering_follows_what_the_level_permits(
        string tier, string ceiling, bool within)
        => KycRemotePolicyValues.TierWithinCeiling(tier, ceiling).Should().Be(within);

    [Theory]
    [InlineData("Full", "Tier9")]
    [InlineData("Full", null)]
    [InlineData(null, "Full")]
    public void An_unreadable_tier_or_ceiling_fails_closed(string? tier, string? ceiling)
    {
        // Reading a corrupted ceiling as "no ceiling" would turn a bad row into permission to
        // grant the full tier with no human. A compliance gate answers no when it cannot tell.
        KycRemotePolicyValues.TierWithinCeiling(tier, ceiling).Should().BeFalse();
    }
}
