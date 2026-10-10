namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The principal's identity as a whole person (BNF-B-02) — the five postal fields Lot 6 added, so
/// the projection can answer <c>WithAddressOf</c> for a household member that inherits it.
/// </summary>
public sealed class PrincipalIdentityTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public PrincipalIdentityTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private Lead Captured(bool withNames = true) => Lead.Capture(
        tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
        source: LeadSource.Agency, interestedProduct: "Assurance santé",
        preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
        clock: TimeProvider.System,
        firstName: withNames ? "Aya" : null, lastName: withNames ? "Kouassi" : null,
        ownerId: Guid.NewGuid());

    [Fact]
    public void The_projection_carries_every_field_the_enrichment_wrote()
    {
        var lead = Captured();

        lead.EnrichPrincipalIdentity(
            null, null, "Aya K.", new DateOnly(1990, 4, 2), LeadGender.Female,
            "12 rue des Jardins", "01 BP 1234", "Abidjan", "ci", Now);

        lead.PrincipalIdentity.Should().NotBeNull();
        var identity = lead.PrincipalIdentity!;
        identity.UsageName.Should().Be("Aya K.");
        identity.BirthDate.Should().Be(new DateOnly(1990, 4, 2));
        identity.Gender.Should().Be(PersonGender.Female);
        identity.AddressLine.Should().Be("12 rue des Jardins");
        identity.PostalCode.Should().Be("01 BP 1234");
        identity.City.Should().Be("Abidjan");
        identity.CountryCode.Should().Be("CI");
    }

    [Fact]
    public void A_malformed_country_code_is_refused_with_a_code()
    {
        var lead = Captured();

        var result = lead.EnrichPrincipalIdentity(
            null, null, null, null, null, null, null, null, "Côte d'Ivoire", Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_PRINCIPAL_COUNTRY_INVALID");
    }

    [Fact]
    public void Without_both_names_there_is_no_identity_at_all()
    {
        // PersonIdentity requires both names, and BNF-B-01 makes every field but the phone
        // optional at capture — so a caller resolving an inherited address has to cope with a
        // principal who has none yet.
        var lead = Captured(withNames: false);

        lead.EnrichPrincipalIdentity(
            null, null, null, null, null, "12 rue des Jardins", null, null, null, Now);

        lead.PrincipalIdentity.Should().BeNull();
        lead.PrincipalAddressLine.Should().Be("12 rue des Jardins",
            "the address is stored either way; only the projection needs the names");
    }

    [Fact]
    public void A_member_inheriting_the_address_now_actually_gets_one()
    {
        // The regression this closes: before the principal had postal fields, WithAddressOf had
        // nothing to copy, so every inheriting member resolved to an address of null.
        var lead = Captured();
        lead.EnrichPrincipalIdentity(
            null, null, null, null, null, "12 rue des Jardins", "01 BP 1234", "Abidjan", "CI", Now);

        var member = lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), null,
            usesPrincipalAddress: true, now: Now).Value;

        var resolved = member.ResolveIdentity(lead.PrincipalIdentity);
        resolved.AddressLine.Should().Be("12 rue des Jardins");
        resolved.City.Should().Be("Abidjan");
    }

    [Fact]
    public async Task The_encrypted_address_round_trips_through_the_converter()
    {
        var lead = Captured();
        lead.EnrichPrincipalIdentity(
            null, null, null, null, null, "12 rue des Jardins", null, "Abidjan", null, Now);

        await using (var write = _factory.CreateContext())
        {
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var stored = await read.Leads.SingleAsync(CancellationToken.None);

        stored.PrincipalAddressLine.Should().Be("12 rue des Jardins");
        stored.PrincipalCity.Should().Be("Abidjan");
    }
}
