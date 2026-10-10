namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Domain.Events;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>Covers PEP exposure of the lead and its household (BNF-B-05).</summary>
public sealed class LeadPepExposureTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const int Window = 12;

    private static Lead Held()
    {
        var lead = Lead.Capture(
            tenantId: Tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System,
            firstName: "Aya", lastName: "Kouassi", ownerId: Guid.NewGuid());
        lead.ClearDomainEvents();
        return lead;
    }

    private static PersonIdentity Person(string first = "Kofi")
        => PersonIdentity.Create("Kouassi", first);

    private static PepStatus Minister(DateOnly? endDate = null, PepLink link = PepLink.Self)
        => PepStatus.Exposed("Ministre des Finances", link, "CI", endDate);

    // ── Defaults ──────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_lead_is_not_exposed()
    {
        var lead = Held();

        lead.PrincipalPep.IsPep.Should().BeFalse();
        lead.HasPepExposure.Should().BeFalse();
    }

    // ── The principal ─────────────────────────────────────────────────────

    [Fact]
    public void Declaring_the_principal_exposed_sets_the_flag_and_raises_the_event()
    {
        var lead = Held();

        lead.SetPrincipalPepStatus(Minister(), Now, Window).IsSuccess.Should().BeTrue();

        lead.HasPepExposure.Should().BeTrue();
        lead.DomainEvents.OfType<LeadPepExposureChangedDomainEvent>()
            .Should().ContainSingle(e => e.HasPepExposure);
    }

    [Fact]
    public void Clearing_the_principals_exposure_clears_the_flag_and_raises_again()
    {
        var lead = Held();
        lead.SetPrincipalPepStatus(Minister(), Now, Window);
        lead.ClearDomainEvents();

        lead.SetPrincipalPepStatus(PepStatus.NotExposed, Now, Window);

        lead.HasPepExposure.Should().BeFalse();
        lead.DomainEvents.OfType<LeadPepExposureChangedDomainEvent>()
            .Should().ContainSingle(e => !e.HasPepExposure);
    }

    [Fact]
    public void An_unchanged_exposure_raises_no_event()
    {
        // The event drives dispatching and a compliance notification; re-firing it on every
        // unrelated save would make both noise.
        var lead = Held();
        lead.SetPrincipalPepStatus(Minister(), Now, Window);
        lead.ClearDomainEvents();

        lead.SetPrincipalPepStatus(Minister(), Now, Window);

        lead.DomainEvents.OfType<LeadPepExposureChangedDomainEvent>().Should().BeEmpty();
    }

    // ── Household propagation (BNF-B-05) ──────────────────────────────────

    [Fact]
    public void A_self_exposed_principal_propagates_FamilyMember_onto_the_household()
    {
        var lead = Held();
        var spouse = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"), null, true, Now).Value;
        var child = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("Kofi"), null, true, Now).Value;

        lead.SetPrincipalPepStatus(Minister(), Now, Window);

        spouse.Pep.IsPep.Should().BeTrue();
        spouse.Pep.Link.Should().Be(PepLink.FamilyMember);
        child.Pep.Link.Should().Be(PepLink.FamilyMember);
    }

    [Fact]
    public void A_member_added_AFTER_the_declaration_inherits_too()
    {
        var lead = Held();
        lead.SetPrincipalPepStatus(Minister(), Now, Window);

        var child = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("Kofi"), null, true, Now).Value;

        child.Pep.Link.Should().Be(PepLink.FamilyMember);
    }

    [Fact]
    public void Propagation_does_NOT_overwrite_a_member_classified_individually()
    {
        // BNF-B-05 allows an agent to depart from the default "avec motif". Overwriting it would
        // discard a judgement the specification explicitly grants them.
        var lead = Held();
        var ownClassification = PepStatus.Exposed(
            "Maire", PepLink.Self, "CI", overrideReason: "exerce son propre mandat");

        var spouse = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"),
            ownClassification, true, Now).Value;

        lead.SetPrincipalPepStatus(Minister(), Now, Window);

        spouse.Pep.Link.Should().Be(PepLink.Self, "the agent's own classification stands");
        spouse.Pep.Function.Should().Be("Maire");
    }

    [Fact]
    public void A_principal_exposed_as_a_family_member_propagates_nothing()
    {
        // Only a Self exposure makes the household family OF a PEP. A principal who is themselves
        // the relative of one does not make their own spouse a relative of that person.
        var lead = Held();
        var spouse = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"), null, true, Now).Value;

        lead.SetPrincipalPepStatus(Minister(link: PepLink.FamilyMember), Now, Window);

        spouse.Pep.IsPep.Should().BeFalse();
        lead.HasPepExposure.Should().BeTrue("the principal is still exposed");
    }

    [Fact]
    public void A_removed_member_is_not_propagated_onto()
    {
        var lead = Held();
        var spouse = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"), null, true, Now).Value;
        lead.RemoveHouseholdMember(spouse.Id, "séparation", Now);

        lead.SetPrincipalPepStatus(Minister(), Now, Window);

        spouse.Pep.IsPep.Should().BeFalse("a removed member is not part of the household assessed");
    }

    // ── Exposure from the household alone ─────────────────────────────────

    [Fact]
    public void An_exposed_spouse_exposes_the_lead()
    {
        var lead = Held();

        lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married, Person("Awa"),
            PepStatus.Exposed("Députée", PepLink.Self, "CI", overrideReason: "mandat propre"),
            true, Now);

        lead.RecalculateHasPepExposure(Now, Window);

        lead.HasPepExposure.Should().BeTrue();
        lead.PrincipalPep.IsPep.Should().BeFalse("the principal is not the exposed one");
    }

    [Fact]
    public void Setting_a_members_status_recalculates_the_lead()
    {
        var lead = Held();
        var child = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null, Person("Kofi"), null, true, Now).Value;
        lead.ClearDomainEvents();

        lead.SetHouseholdMemberPepStatus(
            child.Id,
            PepStatus.Exposed("Conseiller municipal", PepLink.Self, "CI", overrideReason: "mandat propre"),
            Now, Window).IsSuccess.Should().BeTrue();

        lead.HasPepExposure.Should().BeTrue();
        lead.DomainEvents.OfType<LeadPepExposureChangedDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void Setting_the_status_of_an_unknown_member_is_refused()
    {
        Held().SetHouseholdMemberPepStatus(Guid.NewGuid(), Minister(), Now, Window)
            .Error.Should().Be("HOUSEHOLD_MEMBER_NOT_FOUND");
    }

    // ── The post-mandate window ───────────────────────────────────────────

    [Fact]
    public void Exposure_survives_the_end_of_the_mandate_for_the_configured_window()
    {
        // The mandate ended nine months ago; a 12-month window keeps the person exposed.
        var lead = Held();
        lead.SetPrincipalPepStatus(Minister(endDate: new DateOnly(2026, 1, 10)), Now, Window);

        lead.HasPepExposure.Should().BeTrue();
    }

    [Fact]
    public void Exposure_lapses_once_the_window_closes()
    {
        var lead = Held();
        lead.SetPrincipalPepStatus(Minister(endDate: new DateOnly(2024, 1, 10)), Now, Window);

        lead.HasPepExposure.Should().BeFalse("the mandate ended more than twelve months ago");
    }

    [Fact]
    public void A_wider_tenant_window_keeps_the_person_exposed()
    {
        // The tenant parameter is what decides, which is why it is passed in rather than assumed.
        var lead = Held();
        lead.SetPrincipalPepStatus(Minister(endDate: new DateOnly(2024, 1, 10)), Now, postMandateMonths: 36);

        lead.HasPepExposure.Should().BeTrue();
    }

    [Fact]
    public void Recalculating_later_lets_an_exposure_lapse_on_its_own()
    {
        // Nobody touches the lead; the window simply closes. This is why the recalculation is a
        // method the nightly job can call, not a value computed once at declaration.
        var lead = Held();
        lead.SetPrincipalPepStatus(Minister(endDate: new DateOnly(2026, 1, 10)), Now, Window);
        lead.HasPepExposure.Should().BeTrue();
        lead.ClearDomainEvents();

        var later = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        lead.RecalculateHasPepExposure(later, Window);

        lead.HasPepExposure.Should().BeFalse();
        lead.DomainEvents.OfType<LeadPepExposureChangedDomainEvent>()
            .Should().ContainSingle(e => !e.HasPepExposure,
                "dispatching has to learn that the lead no longer needs a cleared agent");
    }

    [Fact]
    public void A_closed_lead_refuses_a_pep_change()
    {
        var lead = Held();
        lead.Convert(Guid.NewGuid());

        lead.SetPrincipalPepStatus(Minister(), Now, Window).IsFailure.Should().BeTrue();
    }
}
