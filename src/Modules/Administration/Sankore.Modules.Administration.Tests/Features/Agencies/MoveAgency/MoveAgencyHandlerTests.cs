namespace Sankore.Modules.Administration.Tests.Features.Agencies.MoveAgency;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.MoveAgency;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

public sealed class MoveAgencyHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public MoveAgencyHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    private MoveAgencyHandler BuildHandler()
        => new(_factory.CreateContext());

    private async Task<(Agency hq, Agency branch, Agency sp)> SeedThreeLevels()
    {
        await using var db = _factory.CreateContext();
        var hq = Agency.Create(_tenantId, "Siège", "", AgencyType.HeadQuarter, null, null);
        var branch = Agency.Create(_tenantId, "Branche", "", AgencyType.Branch, hq.Id, null);
        var sp = Agency.Create(_tenantId, "Point de Service", "", AgencyType.ServicePoint, branch.Id, null);
        db.Agencies.AddRange(hq, branch, sp);
        await db.SaveChangesAsync();
        return (hq, branch, sp);
    }

    // ── S1 : déplacement réussi vers un nouveau parent ──────────────────

    [Fact]
    public async Task Moves_agency_to_a_new_parent_successfully()
    {
        var (hq, branch, sp) = await SeedThreeLevels();

        // Move sp directly under hq (skip branch level)
        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(sp.Id, hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var moved = await db.Agencies.SingleAsync(a => a.Id == sp.Id);
        moved.ParentAgencyId.Should().Be(hq.Id);
        moved.UpdatedAt.Should().NotBeNull();
    }

    // ── S2 : promotion à la racine (HQ sans parent) ─────────────────────

    [Fact]
    public async Task Promotes_hq_to_root_by_clearing_parent()
    {
        await using var db = _factory.CreateContext();
        var hq1 = Agency.Create(_tenantId, "Siège 1", "", AgencyType.HeadQuarter, null, null);
        var hq2 = Agency.Create(_tenantId, "Siège 2", "", AgencyType.HeadQuarter, hq1.Id, null);
        db.Agencies.AddRange(hq1, hq2);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(hq2.Id, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var db2 = _factory.CreateContext();
        var moved = await db2.Agencies.SingleAsync(a => a.Id == hq2.Id);
        moved.ParentAgencyId.Should().BeNull();
    }

    // ── S3 : échec si agence introuvable ─────────────────────────────────

    [Fact]
    public async Task Fails_when_agency_not_found()
    {
        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(Guid.NewGuid(), null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // ── S4 : échec si agence supprimée ───────────────────────────────────

    [Fact]
    public async Task Fails_when_agency_is_deleted()
    {
        var (hq, branch, _) = await SeedThreeLevels();

        await using var db = _factory.CreateContext();
        var b = await db.Agencies.AsTracking().SingleAsync(a => a.Id == branch.Id);
        b.Deactivate();
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(branch.Id, hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("deleted");
    }

    // ── S5 : échec si parent inexistant ──────────────────────────────────

    [Fact]
    public async Task Fails_when_new_parent_does_not_exist()
    {
        var (_, branch, _) = await SeedThreeLevels();

        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(branch.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // ── S6 : échec si référence circulaire directe ────────────────────────

    [Fact]
    public async Task Fails_when_new_parent_is_a_direct_child()
    {
        var (hq, branch, _) = await SeedThreeLevels();

        // Try to make branch the parent of hq (branch is a child of hq)
        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(hq.Id, branch.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("descendants");
    }

    // ── S7 : échec si référence circulaire profonde ──────────────────────

    [Fact]
    public async Task Fails_when_new_parent_is_a_deep_descendant()
    {
        var (hq, _, sp) = await SeedThreeLevels();

        // Try to move hq under sp (sp is 2 levels below hq)
        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(hq.Id, sp.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("descendants");
    }

    // ── S8 : échec si auto-parent ────────────────────────────────────────

    [Fact]
    public async Task Fails_when_agency_set_as_its_own_parent()
    {
        var (hq, _, _) = await SeedThreeLevels();

        var result = await BuildHandler().Handle(
            new MoveAgencyCommand(hq.Id, hq.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }
}
