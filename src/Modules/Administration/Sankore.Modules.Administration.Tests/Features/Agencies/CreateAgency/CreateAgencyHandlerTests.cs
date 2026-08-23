namespace Sankore.Modules.Administration.Tests.Features.Agencies.CreateAgency;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.CreateAgency;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class CreateAgencyHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ICurrentUser _currentUser;

    public CreateAgencyHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Id.Returns(_userId);
    }

    public void Dispose() => _factory.Dispose();

    private CreateAgencyHandler BuildHandler()
        => new(_factory.CreateContext(), _currentUser);

    private static CreateAgencyCommand HqCommand(string name = "Siège Social")
        => new(name, "Description", AgencyType.HeadQuarter, null,
            null, null, null, null, null, null, null);

    private static CreateAgencyCommand BranchCommand(Guid parentId, string name = "Branche Dakar")
        => new(name, "Description", AgencyType.Branch, parentId,
            null, null, null, null, null, null, null);

    // ── S1 : création HQ réussie ────────────────────────────────────────────

    [Fact]
    public async Task Creates_headquarters_agency_successfully()
    {
        var result = await BuildHandler().Handle(HqCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgencyId.Should().NotBeEmpty();
        result.Value.Code.Should().NotBeNullOrWhiteSpace();
    }

    // ── S2 : code généré à partir du nom ────────────────────────────────────

    [Fact]
    public async Task Generated_code_is_derived_from_name()
    {
        var result = await BuildHandler().Handle(HqCommand("Dakar Agency"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("DAKARA"); // first 6 alphanum chars uppercase
    }

    // ── S3 : l'agence est persistée en base ─────────────────────────────────

    [Fact]
    public async Task Agency_is_persisted_with_correct_fields()
    {
        var result = await BuildHandler().Handle(HqCommand("Mon Siège"), CancellationToken.None);

        await using var db = _factory.CreateContext();
        var agency = db.Agencies.SingleOrDefault(a => a.Id == result.Value.AgencyId);

        agency.Should().NotBeNull();
        agency!.Name.Should().Be("Mon Siège");
        agency.TenantId.Should().Be(_tenantId);
        agency.CreatedBy.Should().Be(_userId);
        agency.IsActive.Should().BeTrue();
        agency.IsDeleted.Should().BeFalse();
        agency.AgencyType.Should().Be(AgencyType.HeadQuarter);
        agency.ParentAgencyId.Should().BeNull();
    }

    // ── S4 : création branche avec parent valide ─────────────────────────────

    [Fact]
    public async Task Creates_branch_when_parent_exists()
    {
        // Seed parent HQ first
        await using var db = _factory.CreateContext();
        var hq = Agency.Create(_tenantId, "Siège", "", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(hq);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(BranchCommand(hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db2 = _factory.CreateContext();
        var branch = db2.Agencies.SingleOrDefault(a => a.Id == result.Value.AgencyId);
        branch!.ParentAgencyId.Should().Be(hq.Id);
        branch.AgencyType.Should().Be(AgencyType.Branch);
    }

    // ── S5 : échec si parent inexistant ──────────────────────────────────────

    [Fact]
    public async Task Fails_when_parent_agency_does_not_exist()
    {
        var result = await BuildHandler().Handle(
            BranchCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // ── S6 : adresse optionnelle mappée correctement ─────────────────────────

    [Fact]
    public async Task Creates_agency_with_address_and_coordinates()
    {
        var cmd = new CreateAgencyCommand(
            "Siège Dakar", "Desc", AgencyType.HeadQuarter, null,
            "12 Rue Carnot", "Dakar", "Dakar", "SN", "CP001",
            14.6928, -17.4467);

        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var agency = db.Agencies.Single(a => a.Id == result.Value.AgencyId);
        agency.Address.Should().NotBeNull();
        agency.Address!.Street.Should().Be("12 Rue Carnot");
        agency.Address.City.Should().Be("Dakar");
        agency.Address.Country.Should().Be("SN");
        agency.Address.Location.Should().NotBeNull();
        agency.Address.Location!.Latitude.Should().BeApproximately(14.6928, 0.0001);
        agency.Address.Location.Longitude.Should().BeApproximately(-17.4467, 0.0001);
    }

    // ── S7 : pas d'adresse si street et city sont vides ──────────────────────

    [Fact]
    public async Task Does_not_set_address_when_street_and_city_are_empty()
    {
        var cmd = new CreateAgencyCommand(
            "Siège", "", AgencyType.HeadQuarter, null,
            null, null, "Dakar", "SN", null, null, null);

        var result = await BuildHandler().Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var agency = db.Agencies.Single(a => a.Id == result.Value.AgencyId);
        agency.Address.Should().BeNull();
    }
}
