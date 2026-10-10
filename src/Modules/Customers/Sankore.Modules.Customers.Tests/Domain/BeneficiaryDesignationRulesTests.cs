namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The designation rules of BNF-B-15: roles and cardinalities from the product line's policy, and
/// shares totalling 100% PER RANK when the role demands it.
/// </summary>
public sealed class BeneficiaryDesignationRulesTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private ProductHolding Holding(BeneficiaryPolicyDto? line = null)
        => TestHoldingFactory.Draft(_tenant, _client, line ?? TestHoldingFactory.Line(), _actor, Now);

    private Result<int> Designate(
        ProductHolding holding, IReadOnlyList<DesignationRequest> requests, BeneficiaryPolicyDto line)
        => holding.Designate(requests, line.AllowedRoles, _actor, Now);

    // ── shares ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_single_beneficiary_must_hold_the_whole_capital()
    {
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        Designate(holding, [TestHoldingFactory.Beneficiary(100m)], line)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shares_that_do_not_add_up_are_refused()
    {
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        var result = Designate(holding,
            [
                TestHoldingFactory.Beneficiary(60m, name: "Awa"),
                TestHoldingFactory.Beneficiary(30m, name: "Kofi"),
            ], line);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationSharesMustTotal100);
    }

    [Fact]
    public void Thirds_of_a_capital_are_expressible()
    {
        // 33.33 + 33.33 + 33.34 is the arithmetic an agent actually types, and it is why the
        // column is numeric(5,2) and the comparison is on a decimal rather than a double.
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        var result = Designate(holding,
            [
                TestHoldingFactory.Beneficiary(33.33m, name: "Awa"),
                TestHoldingFactory.Beneficiary(33.33m, name: "Kofi"),
                TestHoldingFactory.Beneficiary(33.34m, name: "Ama"),
            ], line);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public void Each_rank_allocates_the_whole_capital_on_its_own()
    {
        // PER RANK, never across: rank 2 inherits only if rank 1 cannot, so both must total 100.
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        var result = Designate(holding,
            [
                TestHoldingFactory.Beneficiary(100m, rank: 1, name: "Awa"),
                TestHoldingFactory.Beneficiary(100m, rank: 2, name: "Kofi"),
            ], line);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public void A_second_rank_that_does_not_add_up_is_refused_even_when_the_first_does()
    {
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        var result = Designate(holding,
            [
                TestHoldingFactory.Beneficiary(100m, rank: 1, name: "Awa"),
                TestHoldingFactory.Beneficiary(50m, rank: 2, name: "Kofi"),
            ], line);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationSharesMustTotal100);
    }

    [Fact]
    public void A_role_where_a_share_means_nothing_needs_none()
    {
        var line = TestHoldingFactory.Line(roles: [TestHoldingFactory.Proxy]);
        var holding = Holding(line);

        var result = Designate(holding,
            [TestHoldingFactory.Beneficiary(share: null, roleCode: "MANDATAIRE")], line);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    // ── cardinality ─────────────────────────────────────────────────────────

    [Fact]
    public void A_required_role_with_nobody_in_it_is_refused()
    {
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        var result = Designate(holding, [], line);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationRequiredRoleMissing);
    }

    [Fact]
    public void More_designations_than_the_role_allows_are_refused()
    {
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        var result = Designate(holding,
            [
                TestHoldingFactory.Beneficiary(20m, name: "A"),
                TestHoldingFactory.Beneficiary(20m, name: "B"),
                TestHoldingFactory.Beneficiary(20m, name: "C"),
                TestHoldingFactory.Beneficiary(20m, name: "D"),
                TestHoldingFactory.Beneficiary(20m, name: "E"),
            ], line);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationTooMany);
    }

    [Fact]
    public void A_role_the_line_does_not_accept_is_refused()
    {
        // A current account accepts no designation at all, and the policy says so by carrying no
        // role — so every designation on it fails rather than quietly becoming a free-text label.
        var line = TestHoldingFactory.Line(roles: []);
        var holding = Holding(line);

        var result = Designate(holding, [TestHoldingFactory.Beneficiary(100m)], line);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationRoleNotAllowed);
    }

    [Fact]
    public void The_role_code_is_matched_case_insensitively()
    {
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);

        var result = Designate(holding,
            [TestHoldingFactory.Beneficiary(100m, roleCode: "beneficiairedeces")], line);

        result.IsSuccess.Should().BeTrue(result.Error);
        holding.CurrentDesignations.Should().ContainSingle()
               .Which.RoleCode.Should().Be("BENEFICIAIREDECES", "stored upper-cased");
    }

    // ── subjects ────────────────────────────────────────────────────────────

    [Fact]
    public void An_external_person_must_be_named_and_must_carry_no_id()
    {
        var line = TestHoldingFactory.Line();

        var unnamed = Designate(Holding(line),
            [TestHoldingFactory.Beneficiary(100m) with { ExternalFullName = null }], line);

        var withId = Designate(Holding(line),
            [TestHoldingFactory.Beneficiary(100m) with { SubjectId = Guid.NewGuid() }], line);

        unnamed.Error.Should().Be(CustomerErrors.DesignationSubjectInvalid);
        withId.Error.Should().Be(CustomerErrors.DesignationSubjectInvalid,
            "a named subject carrying an id would silently shadow a real client");
    }

    [Fact]
    public void Anybody_else_must_carry_an_id()
    {
        var line = TestHoldingFactory.Line();

        var result = Designate(Holding(line),
            [
                TestHoldingFactory.Beneficiary(
                    100m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: null),
            ], line);

        result.Error.Should().Be(CustomerErrors.DesignationSubjectInvalid);
    }

    [Fact]
    public void An_empty_guid_does_not_pass_for_a_subject()
    {
        // Guid.Empty is what a malformed JSON guid deserialises to in this API.
        var line = TestHoldingFactory.Line();

        var result = Designate(Holding(line),
            [
                TestHoldingFactory.Beneficiary(
                    100m, subjectType: DesignationSubjectType.Client, subjectId: Guid.Empty),
            ], line);

        result.Error.Should().Be(CustomerErrors.DesignationSubjectInvalid);
    }

    [Fact]
    public void The_same_person_twice_in_one_role_and_rank_is_a_duplicate()
    {
        var line = TestHoldingFactory.Line();
        var holding = Holding(line);
        var member = Guid.NewGuid();

        var result = Designate(holding,
            [
                TestHoldingFactory.Beneficiary(
                    50m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: member),
                TestHoldingFactory.Beneficiary(
                    50m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: member),
            ], line);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationDuplicateSubject,
            "two entries for one person is a data-entry slip, not a bigger share");
    }

    [Fact]
    public void A_rank_below_one_is_refused()
    {
        var line = TestHoldingFactory.Line();

        var result = Designate(Holding(line),
            [TestHoldingFactory.Beneficiary(100m, rank: 0)], line);

        result.Error.Should().Be(CustomerErrors.DesignationInvalidRank);
    }

    [Fact]
    public void A_share_outside_zero_to_one_hundred_is_refused()
    {
        var line = TestHoldingFactory.Line();

        var result = Designate(Holding(line),
            [TestHoldingFactory.Beneficiary(140m)], line);

        result.Error.Should().Be(CustomerErrors.DesignationInvalidShare);
    }
}
