namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using System.Text.Json;
using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// The address dimension: what a self-service capture can still earn before anybody calls, and
/// what the identity screen promises next to each field (« Ville · +8 »). The front repeats these
/// weights, so they are pinned here.
/// </summary>
public sealed class AddressScoreTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public AddressScoreTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private Lead UssdLead() => Lead.Capture(
        tenantId: _tenant,
        fullName: "KOFFI BROU",
        phoneNumber: "+2250700000001",
        source: LeadSource.Ussd,
        interestedProduct: "Crédit habitat",
        preferredLanguage: "fr",
        location: new Sankore.Shared.Kernel.ValueObject.GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System);

    private async Task<JsonElement> FactorsAsync(Lead lead)
    {
        await using var db = _factory.CreateContext();
        var (_, factorsJson) = await new LeadScoreCalculator(db).CalculateAsync(lead, CancellationToken.None);
        return JsonDocument.Parse(factorsJson).RootElement;
    }

    private static void Enrich(Lead lead, string? country = null, string? city = null,
        string? neighbourhood = null, string? landmark = null)
        => lead.EnrichPrincipalIdentity(
            firstName: null, lastName: null, usageName: null, birthDate: null, gender: null,
            addressLine: null, postalCode: null, city: city, countryCode: country,
            now: DateTimeOffset.UtcNow, neighbourhood: neighbourhood, landmark: landmark)
            .IsSuccess.Should().BeTrue();

    [Fact]
    public async Task A_lead_without_an_address_scores_nothing_on_it()
    {
        (await FactorsAsync(UssdLead())).GetProperty("address").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Each_part_of_the_address_is_worth_what_the_screen_says()
    {
        var lead = UssdLead();
        Enrich(lead, country: "CI");
        (await FactorsAsync(lead)).GetProperty("address").GetInt32().Should().Be(2);

        Enrich(lead, city: "Abidjan");
        (await FactorsAsync(lead)).GetProperty("address").GetInt32().Should().Be(10);

        Enrich(lead, neighbourhood: "Cocody Angré");
        (await FactorsAsync(lead)).GetProperty("address").GetInt32().Should().Be(16);

        Enrich(lead, landmark: "Derrière la pharmacie");
        (await FactorsAsync(lead)).GetProperty("address").GetInt32().Should().Be(18);
    }

    [Fact]
    public async Task The_address_points_count_in_the_total()
    {
        var lead = UssdLead();
        var before = (await FactorsAsync(lead)).GetProperty("total").GetInt32();

        Enrich(lead, country: "CI", city: "Abidjan", neighbourhood: "Cocody", landmark: "Marché");

        (await FactorsAsync(lead)).GetProperty("total").GetInt32().Should().Be(before + 18);
    }

    [Fact]
    public async Task The_street_alone_earns_nothing()
    {
        // Most of the network has no street worth the name: a street line without a city does not
        // locate anyone, and scoring it would reward the field the agents fill least reliably.
        var lead = UssdLead();
        lead.EnrichPrincipalIdentity(null, null, null, null, null, "Rue 12", "01 BP 1234", null, null,
            DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();

        (await FactorsAsync(lead)).GetProperty("address").GetInt32().Should().Be(0);
    }
}
