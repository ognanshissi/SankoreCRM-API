namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Domain.Events;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>Covers the household invariants on the aggregate (BNF-B-03).</summary>
public sealed class LeadHouseholdTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A lead that has reached an agent — the state the household may be edited in.</summary>
    private static Lead Held(Guid? ownerId = null)
    {
        var lead = Lead.Capture(
            tenantId: Tenant,
            fullName: "Aya Kouassi",
            phoneNumber: "+2250700000001",
            source: LeadSource.Agency,
            interestedProduct: "Assurance santé",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System,
            firstName: "Aya",
            lastName: "Kouassi",
            ownerId: ownerId ?? Guid.NewGuid());

        lead.ClearDomainEvents();
        return lead;
    }

    private static PersonIdentity Person(
        string first = "Kofi", string last = "Kouassi",
        DateOnly? birthDate = null, string? address = null)
        => PersonIdentity.Create(last, first, birthDate: birthDate, addressLine: address);

    // ── The editing gate ──────────────────────────────────────────────────

    [Fact]
    public void A_lead_nobody_holds_is_not_editable()
    {
        // There is no LeadStatus.Assigned: the condition is "live AND somebody is working it".
        var unheld = Lead.Capture(
            tenantId: Tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Web, interestedProduct: "Crédit", preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System);

        unheld.IsHouseholdEditable.Should().BeFalse();

        unheld.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person(), null, true, Now)
            .Error.Should().Be("LEAD_HOUSEHOLD_NOT_EDITABLE");
    }

    [Fact]
    public void An_owner_is_enough_even_with_no_assignment()
    {
        // A walk-in captured at a counter with an owner named and never routed through the engine.
        // Refusing the household there would make the counter path unusable.
        Held().IsHouseholdEditable.Should().BeTrue();
    }

    [Fact]
    public void A_terminal_lead_is_not_editable()
    {
        var lead = Held();
        lead.Convert(Guid.NewGuid());

        lead.IsHouseholdEditable.Should().BeFalse();
    }

    // ── One spouse ────────────────────────────────────────────────────────

    [Fact]
    public void A_second_active_spouse_is_refused()
    {
        var lead = Held();
        lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"), null, true, Now)
            .IsSuccess.Should().BeTrue();

        lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.CivilUnion, Person("Fatou"), null, true, Now)
            .Error.Should().Be("HOUSEHOLD_ALREADY_HAS_SPOUSE");
    }

    [Fact]
    public void A_spouse_can_be_replaced_after_the_first_is_removed()
    {
        // A separation then a remarriage. The removed row stays — a household that forgets a
        // separation cannot answer who was covered when a claim was filed.
        var lead = Held();
        var first = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"), null, true, Now).Value;

        lead.RemoveHouseholdMember(first.Id, "séparation", Now).IsSuccess.Should().BeTrue();

        lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Fatou"), null, true, Now)
            .IsSuccess.Should().BeTrue();

        lead.HouseholdMembers.Should().HaveCount(2, "the removed spouse is kept");
        lead.ActiveHouseholdMembers.Should().ContainSingle();
    }

    [Fact]
    public void Any_number_of_children_is_allowed_without_a_ceiling()
    {
        var lead = Held();

        for (var i = 0; i < 5; i++)
            lead.AddHouseholdMember(
                HouseholdMemberType.Child, null, Person($"Enfant{i}"), null, true, Now)
                .IsSuccess.Should().BeTrue();

        lead.ActiveHouseholdMembers.Should().HaveCount(5);
    }

    // ── Union type pairing ────────────────────────────────────────────────

    [Fact]
    public void A_spouse_without_a_union_type_is_refused()
    {
        Held().AddHouseholdMember(
            HouseholdMemberType.Spouse, null, Person("Awa"), null, true, Now)
            .Error.Should().Be("SPOUSE_UNION_TYPE_REQUIRED");
    }

    [Fact]
    public void A_child_with_a_union_type_is_refused()
    {
        Held().AddHouseholdMember(
            HouseholdMemberType.Child, SpouseUnionType.Married, Person(), null, true, Now)
            .Error.Should().Be("UNION_TYPE_FORBIDDEN_FOR_CHILD");
    }

    // ── Policy limits ─────────────────────────────────────────────────────

    [Fact]
    public void The_child_ceiling_from_the_product_line_is_enforced()
    {
        var lead = Held();
        lead.AddHouseholdMember(HouseholdMemberType.Child, null, Person("A"), null, true, Now, maxChildren: 2);
        lead.AddHouseholdMember(HouseholdMemberType.Child, null, Person("B"), null, true, Now, maxChildren: 2);

        lead.AddHouseholdMember(HouseholdMemberType.Child, null, Person("C"), null, true, Now, maxChildren: 2)
            .Error.Should().Be("HOUSEHOLD_MAX_CHILDREN_REACHED");
    }

    [Fact]
    public void A_removed_child_frees_a_slot()
    {
        var lead = Held();
        var first = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("A"), null, true, Now, maxChildren: 1).Value;

        lead.RemoveHouseholdMember(first.Id, "erreur de saisie", Now);

        lead.AddHouseholdMember(HouseholdMemberType.Child, null, Person("B"), null, true, Now, maxChildren: 1)
            .IsSuccess.Should().BeTrue("the ceiling counts ACTIVE children");
    }

    [Fact]
    public void A_child_over_the_age_limit_is_refused()
    {
        var lead = Held();
        var tooOld = Person("Kofi", birthDate: new DateOnly(2000, 1, 1));   // 26 at Now

        lead.AddHouseholdMember(HouseholdMemberType.Child, null, tooOld, null, true, Now, childMaxAge: 21)
            .Error.Should().Be("HOUSEHOLD_CHILD_TOO_OLD");
    }

    [Fact]
    public void A_child_with_no_birth_date_is_accepted_despite_an_age_limit()
    {
        // BNF-B-01 makes identity fields optional at capture. Refusing the member would lose the
        // household rather than flag the gap — the completeness check reports the missing date.
        Held().AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("Kofi"), null, true, Now, childMaxAge: 21)
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void The_age_limit_does_not_apply_to_a_spouse()
    {
        var elderly = Person("Awa", birthDate: new DateOnly(1950, 1, 1));

        Held().AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, elderly, null, true, Now,
            childMaxAge: 21)
            .IsSuccess.Should().BeTrue();
    }

    // ── Address inheritance is a link, not a copy ─────────────────────────

    [Fact]
    public void An_inheriting_member_stores_no_postal_address()
    {
        // Cleared on WRITE, not merely ignored on read: a stale address left in the row is one a
        // later "stop inheriting" would silently resurrect, years out of date.
        var lead = Held();
        var withAddress = Person("Kofi", address: "Rue de l'ancienne maison");

        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, withAddress, null,
            usesPrincipalAddress: true, now: Now).Value;

        member.Identity.AddressLine.Should().BeNull();
        member.UsesPrincipalAddress.Should().BeTrue();
    }

    [Fact]
    public void A_non_inheriting_member_must_carry_its_own_address()
    {
        Held().AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("Kofi"), null,
            usesPrincipalAddress: false, now: Now)
            .Error.Should().Be("HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS");
    }

    [Fact]
    public void ResolveIdentity_applies_the_principals_address_to_an_inheriting_member()
    {
        var lead = Held();
        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("Kofi"), null, true, Now).Value;

        var principal = PersonIdentity.Create(
            "Kouassi", "Aya", addressLine: "Rue des Jardins 12", city: "Abidjan", countryCode: "CI");

        var resolved = member.ResolveIdentity(principal);

        resolved.AddressLine.Should().Be("Rue des Jardins 12");
        resolved.City.Should().Be("Abidjan");
        resolved.FirstName.Should().Be("Kofi", "only the address is inherited");
    }

    [Fact]
    public void ResolveIdentity_leaves_a_self_addressed_member_alone()
    {
        var lead = Held();
        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("Kofi", address: "Rue de l'internat"), null,
            usesPrincipalAddress: false, now: Now).Value;

        var principal = PersonIdentity.Create(
            "Kouassi", "Aya", addressLine: "Rue des Jardins 12");

        member.ResolveIdentity(principal).AddressLine.Should().Be("Rue de l'internat");
    }

    // ── Removal ───────────────────────────────────────────────────────────

    [Fact]
    public void Removal_is_logical_and_keeps_the_reason()
    {
        var lead = Held();
        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person(), null, true, Now).Value;

        lead.RemoveHouseholdMember(member.Id, "majeur, sorti du foyer", Now).IsSuccess.Should().BeTrue();

        member.IsActive.Should().BeFalse();
        member.RemovedAt.Should().Be(Now);
        member.RemovalReason.Should().Be("majeur, sorti du foyer");
        lead.HouseholdMembers.Should().ContainSingle("the row is never deleted");
    }

    [Fact]
    public void Removal_without_a_reason_is_refused()
    {
        var lead = Held();
        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person(), null, true, Now).Value;

        lead.RemoveHouseholdMember(member.Id, "  ", Now)
            .Error.Should().Be("HOUSEHOLD_REMOVAL_REASON_REQUIRED");
        member.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Removing_twice_is_refused()
    {
        var lead = Held();
        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person(), null, true, Now).Value;

        lead.RemoveHouseholdMember(member.Id, "sorti du foyer", Now);

        lead.RemoveHouseholdMember(member.Id, "encore", Now)
            .Error.Should().Be("HOUSEHOLD_MEMBER_ALREADY_REMOVED");
    }

    [Fact]
    public void Removing_an_unknown_member_is_refused()
    {
        Held().RemoveHouseholdMember(Guid.NewGuid(), "peu importe", Now)
            .Error.Should().Be("HOUSEHOLD_MEMBER_NOT_FOUND");
    }

    // ── Events ────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_and_removing_each_raise_a_household_event()
    {
        var lead = Held();

        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"), null, true, Now).Value;

        lead.DomainEvents.OfType<LeadHouseholdChangedDomainEvent>()
            .Should().ContainSingle(e => e.Added && e.MemberId == member.Id);

        lead.ClearDomainEvents();
        lead.RemoveHouseholdMember(member.Id, "séparation", Now);

        lead.DomainEvents.OfType<LeadHouseholdChangedDomainEvent>()
            .Should().ContainSingle(e => !e.Added && e.MemberId == member.Id);
    }
}
