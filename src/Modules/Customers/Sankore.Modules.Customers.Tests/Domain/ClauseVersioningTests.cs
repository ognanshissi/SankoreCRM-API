namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// BNF-B-15's versioning: on a LIVE holding a change to the clause is an avenant — a new version,
/// with the previous one closed rather than edited.
/// </summary>
public sealed class ClauseVersioningTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly Sankore.Modules.Administration.PublicApi.BeneficiaryPolicyDto _line =
        TestHoldingFactory.Line();

    private ProductHolding Active()
    {
        var holding = TestHoldingFactory.Draft(_tenant, _client, _line, _actor, Now);
        holding.Designate([TestHoldingFactory.Beneficiary(100m, name: "Awa")], _line.AllowedRoles, _actor, Now);
        holding.RequestSignature([], Now);
        holding.Sign("doc://signature/1", Now, Now);
        holding.SubmitForSync(Now);
        holding.ConfirmSync("Orass", "POL-1", Now);
        return holding;
    }

    [Fact]
    public void Editing_a_draft_clause_replaces_it_without_versioning()
    {
        // Before the contract is live the clause is still being captured, and versioning every
        // keystroke would bury the real avenants.
        var holding = TestHoldingFactory.Draft(_tenant, _client, _line, _actor, Now);

        holding.Designate([TestHoldingFactory.Beneficiary(100m, name: "Awa")], _line.AllowedRoles, _actor, Now);
        var second = holding.Designate(
            [TestHoldingFactory.Beneficiary(100m, name: "Kofi")], _line.AllowedRoles, _actor, Now);

        second.Value.Should().Be(1, "still version 1");
        holding.ClauseVersion.Should().Be(1);
        holding.CurrentDesignations.Should().ContainSingle()
               .Which.ExternalFullName.Should().Be("Kofi");
    }

    [Fact]
    public void Changing_a_live_clause_writes_a_new_version()
    {
        var holding = Active();

        var version = holding.Designate(
            [
                TestHoldingFactory.Beneficiary(50m, name: "Awa"),
                TestHoldingFactory.Beneficiary(50m, name: "Kofi"),
            ], _line.AllowedRoles, _actor, Now.AddDays(30));

        version.Value.Should().Be(2);
        holding.ClauseVersion.Should().Be(2);

        holding.CurrentDesignations.Should().HaveCount(2);
        holding.CurrentDesignations.Should().OnlyContain(d => d.ClauseVersion == 2);
    }

    [Fact]
    public void The_previous_version_is_closed_and_kept()
    {
        var holding = Active();
        var later = Now.AddDays(30);

        holding.Designate([TestHoldingFactory.Beneficiary(100m, name: "Kofi")], _line.AllowedRoles, _actor, later);

        var superseded = holding.Designations
            .Should().Contain(d => d.ClauseVersion == 1).Which;

        superseded.Status.Should().Be(DesignationStatus.Closed);
        superseded.ValidTo.Should().Be(later, "the old clause is closed AT the moment of the avenant");
        superseded.StatusReason.Should().Be("SUPERSEDED");
        superseded.ExternalFullName.Should().Be("Awa", "the old clause is still readable");
    }

    [Fact]
    public void A_clause_cannot_change_while_the_subscription_is_in_a_queue()
    {
        // The request is already on its way, and a clause changing underneath it would be sent as
        // neither version.
        var holding = TestHoldingFactory.Draft(_tenant, _client, _line, _actor, Now);
        holding.Designate([TestHoldingFactory.Beneficiary(100m)], _line.AllowedRoles, _actor, Now);
        holding.RequestSignature([], Now);
        holding.Sign("doc://signature/1", Now, Now);
        holding.SubmitForSync(Now);

        var result = holding.Designate(
            [TestHoldingFactory.Beneficiary(100m, name: "Kofi")], _line.AllowedRoles, _actor, Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.HoldingSyncInProgress);
    }

    // ── a departing household member (BNF-B-13) ─────────────────────────────

    [Fact]
    public void A_departing_members_designations_are_flagged_not_deleted()
    {
        var member = Guid.NewGuid();
        var holding = TestHoldingFactory.Draft(_tenant, _client, _line, _actor, Now);

        holding.Designate(
            [
                TestHoldingFactory.Beneficiary(
                    60m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: member),
                TestHoldingFactory.Beneficiary(40m, name: "Kofi"),
            ], _line.AllowedRoles, _actor, Now);

        var flagged = holding.FlagDesignationsOf(member, "HOUSEHOLD_MEMBER_REMOVED");

        flagged.Should().Be(1);

        var affected = holding.Designations.Single(d => d.SubjectId == member);
        affected.Status.Should().Be(DesignationStatus.ToReview);
        affected.StatusReason.Should().Be("HOUSEHOLD_MEMBER_REMOVED");
        affected.SharePercent.Should().Be(60m,
            "the share is NOT reallocated — the rank no longer totals 100 and that is what the "
            + "agent has to be shown");

        holding.Designations.Single(d => d.ExternalFullName == "Kofi")
               .Status.Should().Be(DesignationStatus.Active);
    }

    [Fact]
    public void Flagging_is_idempotent_and_ignores_everybody_else()
    {
        var member = Guid.NewGuid();
        var holding = TestHoldingFactory.Draft(_tenant, _client, _line, _actor, Now);

        holding.Designate(
            [
                TestHoldingFactory.Beneficiary(
                    100m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: member),
            ], _line.AllowedRoles, _actor, Now);

        holding.FlagDesignationsOf(member, "HOUSEHOLD_MEMBER_REMOVED").Should().Be(1);
        holding.FlagDesignationsOf(member, "HOUSEHOLD_MEMBER_REMOVED").Should().Be(0,
            "already flagged — a replayed removal must not re-notify");
        holding.FlagDesignationsOf(Guid.NewGuid(), "HOUSEHOLD_MEMBER_REMOVED").Should().Be(0);
    }

    [Fact]
    public void The_same_person_may_be_designated_on_several_holdings()
    {
        // BNF-B-15 is explicit, and it is why nothing here is unique per subject.
        var member = Guid.NewGuid();
        var first = TestHoldingFactory.Draft(_tenant, _client, _line, _actor, Now);
        var second = TestHoldingFactory.Draft(_tenant, _client, _line, _actor, Now);

        var designation = TestHoldingFactory.Beneficiary(
            100m, subjectType: DesignationSubjectType.HouseholdMember, subjectId: member);

        first.Designate([designation], _line.AllowedRoles, _actor, Now).IsSuccess.Should().BeTrue();
        second.Designate([designation], _line.AllowedRoles, _actor, Now).IsSuccess.Should().BeTrue();
    }
}
