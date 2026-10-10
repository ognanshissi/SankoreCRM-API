namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// "Not exposed" and "never asked" are different answers (BNF-B-05), and an LBC/FT file cannot
/// treat them as one. <c>PepStatus.NotExposed</c> is the owned type's default, so the status alone
/// cannot carry the difference — <c>PepDeclaredAt</c> does.
/// </summary>
public sealed class PepDeclarationTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public PepDeclarationTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private Lead Held() => Lead.Capture(
        tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
        source: LeadSource.Agency, interestedProduct: "Assurance santé",
        preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
        clock: TimeProvider.System,
        firstName: "Aya", lastName: "Kouassi", ownerId: Guid.NewGuid());

    [Fact]
    public void A_freshly_captured_lead_has_declared_nothing()
    {
        var lead = Held();

        lead.PrincipalPep.IsPep.Should().BeFalse();
        lead.PepDeclaredAt.Should().BeNull("nobody has been asked yet");
    }

    [Fact]
    public void Declaring_NOT_exposed_is_still_a_declaration()
    {
        var lead = Held();

        lead.SetPrincipalPepStatus(PepStatus.NotExposed, Now, postMandateMonths: 12);

        lead.PrincipalPep.IsPep.Should().BeFalse();
        lead.PepDeclaredAt.Should().Be(Now);
    }

    [Fact]
    public void A_member_added_without_a_PEP_status_has_not_been_asked()
    {
        var lead = Held();

        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), pep: null,
            usesPrincipalAddress: true, now: Now);

        member.Value.PepDeclaredAt.Should().BeNull(
            "defaulting the stamp would record an answer nobody gave");
    }

    [Fact]
    public void A_member_added_WITH_one_has_been()
    {
        var lead = Held();

        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), pep: PepStatus.NotExposed,
            usesPrincipalAddress: true, now: Now);

        member.Value.PepDeclaredAt.Should().Be(Now);
    }

    [Fact]
    public void The_principals_propagated_exposure_counts_as_a_declaration_about_the_member()
    {
        // The propagation IS a decision about the member — made by the rule rather than by the
        // agent, but recorded all the same (BNF-B-05).
        var lead = Held();
        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married,
            PersonIdentity.Create("Kouassi", "Awa"), pep: null,
            usesPrincipalAddress: true, now: Now).Value;

        member.PepDeclaredAt.Should().BeNull();

        var later = Now.AddDays(1);
        lead.SetPrincipalPepStatus(
            PepStatus.Exposed("Députée", PepLink.Self, "CI"), later, postMandateMonths: 12);

        member.Pep.Link.Should().Be(PepLink.FamilyMember);
        member.PepDeclaredAt.Should().Be(later);
    }

    [Fact]
    public async Task The_declaration_timestamp_round_trips()
    {
        var lead = Held();
        lead.SetPrincipalPepStatus(PepStatus.NotExposed, Now, postMandateMonths: 12);
        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), pep: PepStatus.NotExposed,
            usesPrincipalAddress: true, now: Now);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.Leads
            .Include(l => l.HouseholdMembers)
            .SingleAsync(CancellationToken.None);

        stored.PepDeclaredAt.Should().Be(Now);
        stored.HouseholdMembers.Should().ContainSingle()
              .Which.PepDeclaredAt.Should().Be(Now);
    }
}
