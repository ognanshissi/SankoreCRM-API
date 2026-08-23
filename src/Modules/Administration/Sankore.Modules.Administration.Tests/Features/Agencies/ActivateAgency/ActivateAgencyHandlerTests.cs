namespace Sankore.Modules.Administration.Tests.Features.Agencies.ActivateAgency;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.ActivateAgency;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class ActivateAgencyHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ICurrentUser _currentUser;

    public ActivateAgencyHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    private ActivateAgencyHandler BuildHandler()
        => new(_factory.CreateContext(), _currentUser);

    private async Task<Agency> SeedDeletedHq()
    {
        await using var db = _factory.CreateContext();
        var hq = Agency.Create(_tenantId, "Siège", "Desc", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(hq);
        await db.SaveChangesAsync();

        await using var db2 = _factory.CreateContext();
        var agency = await db2.Agencies.AsTracking().SingleAsync(a => a.Id == hq.Id);
        agency.Deactivate();
        await db2.SaveChangesAsync();

        return hq;
    }

    // ── S1 : réactivation réussie ─────────────────────────────────────────

    [Fact]
    public async Task Activates_a_deactivated_agency_successfully()
    {
        var hq = await SeedDeletedHq();

        var result = await BuildHandler().Handle(
            new ActivateAgencyCommand(hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var agency = await db.Agencies.SingleAsync(a => a.Id == hq.Id);
        agency.IsDeleted.Should().BeFalse();
        agency.IsActive.Should().BeTrue();
        agency.UpdatedAt.Should().NotBeNull();
    }

    // ── S2 : échec si agence introuvable ──────────────────────────────────

    [Fact]
    public async Task Fails_when_agency_not_found()
    {
        var result = await BuildHandler().Handle(
            new ActivateAgencyCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // ── S3 : échec si agence déjà active ─────────────────────────────────

    [Fact]
    public async Task Fails_when_agency_is_not_deactivated()
    {
        await using var db = _factory.CreateContext();
        var hq = Agency.Create(_tenantId, "Siège", "Desc", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(hq);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new ActivateAgencyCommand(hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not deactivated");
    }

    // ── S4 : isolation tenant ─────────────────────────────────────────────

    [Fact]
    public async Task Cannot_activate_agency_from_another_tenant()
    {
        var otherTenantId = Guid.NewGuid();
        var otherFactory = new TestAdminDbContextFactory(otherTenantId);

        await using var db = otherFactory.CreateContext();
        var hq = Agency.Create(otherTenantId, "Autre Siège", "", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(hq);
        await db.SaveChangesAsync();

        await using var db2 = otherFactory.CreateContext();
        var agency = await db2.Agencies.AsTracking().SingleAsync(a => a.Id == hq.Id);
        agency.Deactivate();
        await db2.SaveChangesAsync();

        // Handler is bound to _tenantId, agency belongs to otherTenantId
        var result = await BuildHandler().Handle(
            new ActivateAgencyCommand(hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");

        otherFactory.Dispose();
    }
}
