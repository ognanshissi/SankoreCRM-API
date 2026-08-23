namespace Sankore.Modules.Administration.Tests.Features.Agencies.UpdateAgency;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.UpdateAgency;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

public sealed class UpdateAgencyHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public UpdateAgencyHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    private UpdateAgencyHandler BuildHandler()
        => new(_factory.CreateContext());

    private static UpdateAgencyCommand BasicUpdate(Guid agencyId, string name = "Nouveau Nom")
        => new(agencyId, name, "Nouvelle description", AgencyType.Branch,
            null, null, null, null, null, null, null);

    private async Task<Agency> SeedHq(string name = "Siège")
    {
        await using var db = _factory.CreateContext();
        var hq = Agency.Create(_tenantId, name, "Desc", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(hq);
        await db.SaveChangesAsync();
        return hq;
    }

    // ── S1 : mise à jour réussie ────────────────────────────────────────────

    [Fact]
    public async Task Updates_agency_name_and_description_successfully()
    {
        var hq = await SeedHq();

        var result = await BuildHandler().Handle(
            new UpdateAgencyCommand(hq.Id, "Nouveau Nom", "Nouvelle Desc",
                AgencyType.HeadQuarter, null, null, null, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var updated = await db.Agencies.SingleAsync(a => a.Id == hq.Id);
        updated.Name.Should().Be("Nouveau Nom");
        updated.Description.Should().Be("Nouvelle Desc");
        updated.UpdatedAt.Should().NotBeNull();
    }

    // ── S2 : type d'agence modifié ───────────────────────────────────────────

    [Fact]
    public async Task Updates_agency_type()
    {
        var hq = await SeedHq();

        var result = await BuildHandler().Handle(
            BasicUpdate(hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var updated = await db.Agencies.SingleAsync(a => a.Id == hq.Id);
        updated.AgencyType.Should().Be(AgencyType.Branch);
    }

    // ── S3 : mise à jour de l'adresse ───────────────────────────────────────

    [Fact]
    public async Task Updates_address_and_coordinates()
    {
        var hq = await SeedHq();

        var result = await BuildHandler().Handle(
            new UpdateAgencyCommand(hq.Id, "Siège", "Desc", AgencyType.HeadQuarter,
                "10 Avenue Bourguiba", "Dakar", "Dakar", "SN", "BP100",
                14.6937, -17.4441),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var updated = await db.Agencies.SingleAsync(a => a.Id == hq.Id);
        updated.Address.Should().NotBeNull();
        updated.Address!.Street.Should().Be("10 Avenue Bourguiba");
        updated.Address.City.Should().Be("Dakar");
        updated.Address.Location!.Latitude.Should().BeApproximately(14.6937, 0.0001);
    }

    // ── S4 : adresse effacée si street+city vides ─────────────────────────────

    [Fact]
    public async Task Clears_address_when_street_and_city_are_empty()
    {
        var hq = await SeedHq();

        // First set an address
        await BuildHandler().Handle(
            new UpdateAgencyCommand(hq.Id, "Siège", "", AgencyType.HeadQuarter,
                "Rue X", "Dakar", null, null, null, null, null),
            CancellationToken.None);

        // Then clear it
        var result = await BuildHandler().Handle(
            new UpdateAgencyCommand(hq.Id, "Siège", "", AgencyType.HeadQuarter,
                null, null, null, null, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var updated = await db.Agencies.SingleAsync(a => a.Id == hq.Id);
        updated.Address.Should().BeNull();
    }

    // ── S5 : échec si agence introuvable ─────────────────────────────────────

    [Fact]
    public async Task Fails_when_agency_not_found()
    {
        var result = await BuildHandler().Handle(
            BasicUpdate(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // ── S6 : échec si agence supprimée ───────────────────────────────────────

    [Fact]
    public async Task Fails_when_agency_is_deleted()
    {
        var hq = await SeedHq();

        await using var db = _factory.CreateContext();
        var agency = await db.Agencies.AsTracking().SingleAsync(a => a.Id == hq.Id);
        agency.Deactivate();
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            BasicUpdate(hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("deleted");
    }
}
