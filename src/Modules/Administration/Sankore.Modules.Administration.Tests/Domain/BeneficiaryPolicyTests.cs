namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel.ValueObject;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Covers <see cref="BeneficiaryPolicy"/> and its role definitions (BNF-B-06).</summary>
public sealed class BeneficiaryPolicyTests
{
    private static DesignationRoleDefinition Role(
        string code = "BENEFICIAIREDECES",
        string label = "Bénéficiaire en cas de décès",
        int min = 0,
        int? max = null,
        bool shares = false)
        => new(code, label, min, max, shares);

    private static Result<BeneficiaryPolicy> Create(
        IReadOnlyList<DesignationRoleDefinition>? roles = null,
        int? maxChildren = null,
        int? childMaxAge = null,
        IReadOnlyList<string>? requiredFields = null,
        string? kycTier = null,
        string? integrationSystem = null)
        => BeneficiaryPolicy.Create(
            roles, maxChildren, childMaxAge, requiredFields, kycTier, integrationSystem);

    // ── The unconfigured default ──────────────────────────────────────────

    [Fact]
    public void None_is_the_most_restrictive_reading()
    {
        // A line nobody has set up designates nobody and requires nothing. It says nothing about
        // the household any more: the flag that used to live here defaulted to false and so made
        // this default refuse a spouse on every unconfigured line.
        var none = BeneficiaryPolicy.None;

        none.Roles.Should().BeEmpty();
        none.RequiredFields.Should().BeEmpty();
        none.MaxChildren.Should().BeNull();
        none.RequiredKycTier.Should().BeNull();
        none.IntegrationSystem.Should().BeNull();
    }

    // ── Normalisation ─────────────────────────────────────────────────────

    [Fact]
    public void Role_codes_are_upper_cased_and_labels_trimmed()
    {
        var policy = Create(roles: [Role(code: " beneficiaireDeces ", label: "  Bénéficiaire  ")]);

        policy.IsSuccess.Should().BeTrue();
        policy.Value.Roles[0].Code.Should().Be("BENEFICIAIREDECES");
        policy.Value.Roles[0].Label.Should().Be("Bénéficiaire");
    }

    [Fact]
    public void Role_lookup_is_case_insensitive()
    {
        var policy = Create(roles: [Role(code: "COTITULAIRE")]).Value;

        policy.Role("cotitulaire").Should().NotBeNull();
        policy.Role("CoTitulaire").Should().NotBeNull();
        policy.Role("MANDATAIRE").Should().BeNull();
    }

    [Fact]
    public void Required_fields_are_normalised_and_deduplicated()
    {
        // Trimmed, upper-cased and deduplicated through PersonFieldCode, so a stored policy holds
        // one spelling per field and M13's lookup never has to care how it was typed.
        var policy = Create(requiredFields:
            [" birth_date ", "BIRTH_DATE", "  ", "national_id"]);

        policy.Value.RequiredFields.Should().BeEquivalentTo(
            [PersonFieldCode.BirthDate, PersonFieldCode.NationalId]);
    }

    [Fact]
    public void A_required_field_nothing_can_evaluate_is_refused_at_configuration_time()
    {
        // The whole point of the closed list: free text meant a typo was a requirement that
        // silently never applied, which is the one outcome a conversion gate must not have.
        var policy = Create(requiredFields: [PersonFieldCode.BirthDate, "favouriteColour"]);

        policy.IsFailure.Should().BeTrue();
        policy.Error.Should().Be(
            "BENEFICIARY_POLICY_UNKNOWN_REQUIRED_FIELD:favouriteColour",
            "the administrator has to be told WHICH code was rejected");
    }

    [Fact]
    public void Blank_kyc_tier_and_integration_system_become_null()
    {
        var policy = Create(kycTier: "   ", integrationSystem: "");

        policy.Value.RequiredKycTier.Should().BeNull();
        policy.Value.IntegrationSystem.Should().BeNull();
    }

    // ── Fields that constrain each other ──────────────────────────────────

    [Fact]
    public void Child_limits_are_stored_as_submitted()
    {
        // They used to be blanked unless a `householdEnabled` flag was also set — and since that
        // flag defaulted to false, an administrator who typed "at most 4 children" without ticking
        // a separate box had the limit silently discarded. A household is recordable on every line,
        // so a ceiling always means something.
        var policy = Create(maxChildren: 4, childMaxAge: 21);

        policy.Value.MaxChildren.Should().Be(4);
        policy.Value.ChildMaxAge.Should().Be(21);
    }

    // ── Refusals ──────────────────────────────────────────────────────────

    [Fact]
    public void Two_roles_sharing_a_code_are_refused()
    {
        // Case-insensitively: Role() would otherwise return whichever happened to come first.
        var policy = Create(roles: [Role(code: "COTITULAIRE"), Role(code: "cotitulaire")]);

        policy.IsFailure.Should().BeTrue();
        policy.Error.Should().Be("BENEFICIARY_POLICY_DUPLICATE_ROLE");
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    [InlineData(null, 131)]
    public void Impossible_child_limits_are_refused(int? maxChildren, int? childMaxAge)
    {
        Create(maxChildren: maxChildren, childMaxAge: childMaxAge)
            .Error.Should().Be("BENEFICIARY_POLICY_INVALID_CHILD_LIMIT");
    }

    [Fact]
    public void A_role_with_no_code_is_refused()
    {
        Create(roles: [Role(code: "  ")]).Error.Should().Be("DESIGNATION_ROLE_CODE_REQUIRED");
    }

    [Fact]
    public void A_role_with_no_label_is_refused()
    {
        // The label is what an agent reads, in the institution's own wording.
        Create(roles: [Role(label: " ")]).Error.Should().Be("DESIGNATION_ROLE_LABEL_REQUIRED");
    }

    [Theory]
    [InlineData(0, 0)]      // max below 1
    [InlineData(3, 2)]      // max below min
    [InlineData(-1, null)]  // negative min
    public void Impossible_cardinalities_are_refused(int min, int? max)
    {
        Create(roles: [Role(min: min, max: max)])
            .Error.Should().Be("DESIGNATION_ROLE_INVALID_CARDINALITY");
    }

    [Fact]
    public void A_share_rule_on_a_single_mandatory_slot_is_refused_as_pointless()
    {
        // One designation holding 100% is the only way its shares can add up, so the rule does
        // nothing — and an administrator who ticked that box believes it does something.
        Create(roles: [Role(min: 1, max: 1, shares: true)])
            .Error.Should().Be("DESIGNATION_ROLE_SHARE_RULE_POINTLESS");
    }

    [Fact]
    public void A_share_rule_on_several_slots_is_accepted()
    {
        Create(roles: [Role(min: 1, max: 4, shares: true)]).IsSuccess.Should().BeTrue();
    }

    // ── A realistic health-insurance line ─────────────────────────────────

    [Fact]
    public void A_health_insurance_line_configures_household_roles_and_gates()
    {
        var policy = Create(
            roles:
            [
                Role("AYANTDROITSANTE", "Ayant droit santé", min: 0, max: 10),
                Role("BENEFICIAIREDECES", "Bénéficiaire décès", min: 1, max: 4, shares: true),
            ],
            maxChildren: 10,
            childMaxAge: 21,
            requiredFields:
                [PersonFieldCode.BirthDate, PersonFieldCode.Address, PersonFieldCode.PepStatus],
            kycTier: "Full",
            integrationSystem: "Orass");

        policy.IsSuccess.Should().BeTrue();

        var value = policy.Value;
        value.Roles.Should().HaveCount(2);
        value.Role("ayantdroitsante")!.MaxCount.Should().Be(10);
        value.Role("beneficiairedeces")!.SharesMustTotal100.Should().BeTrue();
        value.RequiredFields.Should().HaveCount(3);
        value.RequiredKycTier.Should().Be("Full");
        value.IntegrationSystem.Should().Be("Orass");
    }
}
