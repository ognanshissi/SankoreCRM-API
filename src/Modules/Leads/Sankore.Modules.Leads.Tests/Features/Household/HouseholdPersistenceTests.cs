namespace Sankore.Modules.Leads.Tests.Features.Household;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers the household as STORED: the shared PersonIdentity/PepStatus mapping, the encryption of
/// the sensitive fields, and the tracking traps an owned type brings (BNF-B-02, BNF-B-03).
/// </summary>
public sealed class HouseholdPersistenceTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public HouseholdPersistenceTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private Lead Held() => Lead.Capture(
        tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
        source: LeadSource.Agency, interestedProduct: "Assurance santé",
        preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
        clock: TimeProvider.System,
        firstName: "Aya", lastName: "Kouassi", ownerId: Guid.NewGuid());

    [Fact]
    public async Task A_household_member_round_trips_with_its_identity_and_pep()
    {
        var lead = Held();
        lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.CivilUnion,
            PersonIdentity.Create(
                "Kouassi", "Awa",
                birthDate: new DateOnly(1990, 4, 2),
                gender: PersonGender.Female,
                addressLine: "Rue des Jardins 12",
                city: "Abidjan",
                countryCode: "CI"),
            PepStatus.Exposed("Députée", PepLink.Self, "CI", overrideReason: "mandat propre"),
            usesPrincipalAddress: false, now: Now);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.Leads
            .Include(l => l.HouseholdMembers)
            .SingleAsync(CancellationToken.None);

        var member = stored.HouseholdMembers.Should().ContainSingle().Subject;

        member.Type.Should().Be(HouseholdMemberType.Spouse);
        member.SpouseUnionType.Should().Be(SpouseUnionType.CivilUnion);
        member.Identity.FirstName.Should().Be("Awa");
        // Encrypted at rest and transparent on read — the value converter reverses it.
        member.Identity.BirthDate.Should().Be(new DateOnly(1990, 4, 2));
        member.Identity.AddressLine.Should().Be("Rue des Jardins 12");
        member.Identity.CountryCode.Should().Be("CI");
        member.Pep.IsPep.Should().BeTrue();
        member.Pep.Function.Should().Be("Députée");
        member.Pep.Link.Should().Be(PepLink.Self);
        member.Pep.OverrideReason.Should().Be("mandat propre",
            "an individually classified member keeps its reason through the round trip");
    }

    [Fact]
    public async Task Several_leads_each_get_their_own_unexposed_pep_row()
    {
        // The trap that broke the end-to-end import: PepStatus.NotExposed is an OWNED entity, and
        // a single shared static instance makes the second lead's save fail with
        // "part of a key and so cannot be modified". It must hand back a fresh instance.
        await using var db = _factory.CreateContext();

        for (var i = 0; i < 3; i++)
            db.Leads.Add(Held());

        var act = async () => await db.SaveChangesAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        (await db.Leads.CountAsync(CancellationToken.None)).Should().Be(3);
    }

    [Fact]
    public async Task An_inheriting_member_stores_no_address_and_resolves_one_on_read()
    {
        var lead = Held();
        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi", birthDate: new DateOnly(2015, 6, 1)),
            null, usesPrincipalAddress: true, now: Now);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.Leads
            .Include(l => l.HouseholdMembers)
            .SingleAsync(CancellationToken.None);

        var member = stored.HouseholdMembers.Single();
        member.Identity.AddressLine.Should().BeNull("the row holds no address of its own");

        var principal = PersonIdentity.Create(
            "Kouassi", "Aya", addressLine: "Rue des Jardins 12", city: "Abidjan");

        member.ResolveIdentity(principal).AddressLine.Should().Be("Rue des Jardins 12");
    }

    [Fact]
    public async Task A_removed_member_is_still_there_after_a_round_trip()
    {
        var lead = Held();
        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married,
            PersonIdentity.Create("Kouassi", "Awa"), null, true, Now).Value;

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using (var remove = _factory.CreateContext())
        {
            var tracked = await remove.Leads
                .AsTracking().Include(l => l.HouseholdMembers)
                .SingleAsync(CancellationToken.None);

            tracked.RemoveHouseholdMember(member.Id, "séparation", Now).IsSuccess.Should().BeTrue();
            await remove.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.Leads
            .Include(l => l.HouseholdMembers)
            .SingleAsync(CancellationToken.None);

        stored.HouseholdMembers.Should().ContainSingle();
        stored.HouseholdMembers[0].IsActive.Should().BeFalse();
        stored.HouseholdMembers[0].RemovalReason.Should().Be("séparation");
    }

    [Fact]
    public async Task The_exposure_flag_is_a_column_so_it_survives_a_reload()
    {
        // Stored rather than computed, so dispatching can filter on it in SQL and so the change
        // can raise its event at the moment it happens.
        var lead = Held();
        lead.SetPrincipalPepStatus(
            PepStatus.Exposed("Ministre", PepLink.Self, "CI"), Now, postMandateMonths: 12);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.Leads.SingleAsync(CancellationToken.None);

        stored.HasPepExposure.Should().BeTrue();
        stored.PrincipalPep.Function.Should().Be("Ministre");
        stored.PrincipalPep.CountryCode.Should().Be("CI");
    }

    [Fact]
    public async Task A_member_added_to_a_RELOADED_lead_is_tracked_as_Added_and_saves()
    {
        // The regression this test exists for, and it was a real bug that shipped past the rest of
        // this file: every other case here adds members to a lead that is itself NEW, where the
        // whole graph is Added and the child's state is inherited. The endpoint does
        // load → add → save, and EF treats a Guid key that is already set as store-generated —
        // so the member came out Modified and the save tried to UPDATE a row never inserted
        // ("Attempted to update or delete an entity that does not exist in the store").
        //
        // ValueGeneratedNever on the key is the fix. This asserts the STATE as well as the save,
        // because a future mapping change could make the save pass for an unrelated reason.
        var lead = Held();

        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers)
            .SingleAsync(CancellationToken.None);

        tracked.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), null, usesPrincipalAddress: true, now: Now);

        db.ChangeTracker.Entries<LeadHouseholdMember>().Single().State
            .Should().Be(EntityState.Added);

        var act = async () => await db.SaveChangesAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        await using var read = _factory.CreateContext();
        (await read.LeadHouseholdMembers.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task One_tenants_household_is_invisible_to_another()
    {
        var lead = Held();
        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), null, true, Now);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        using var otherTenant = new TestDbContextFactory(Guid.NewGuid());
        await using var read = otherTenant.CreateContext();

        // A different in-memory database as well as a different tenant filter — the assertion is
        // that nothing leaks, which both mechanisms must deliver.
        (await read.LeadHouseholdMembers.CountAsync(CancellationToken.None)).Should().Be(0);
    }
}
