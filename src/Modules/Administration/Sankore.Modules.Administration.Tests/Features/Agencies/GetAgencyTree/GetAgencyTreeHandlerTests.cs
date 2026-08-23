namespace Sankore.Modules.Administration.Tests.Features.Agencies.GetAgencyTree;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.GetAgencyTree;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Verifies GetAgencyTreeHandler against a real EF Core InMemory DbContext so
/// that the global TenantId query filter and the in-memory tree assembly are
/// genuinely exercised without any external dependencies.
/// </summary>
public sealed class GetAgencyTreeHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public GetAgencyTreeHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Agency HQ(Guid tenantId, string name = "Siège Social")
        => Agency.Create(tenantId, name, "", AgencyType.HeadQuarter, null, null);

    private static Agency Branch(Guid tenantId, Guid parentId, string name = "Branche Dakar")
        => Agency.Create(tenantId, name, "", AgencyType.Branch, parentId, null);

    private static Agency ServicePoint(Guid tenantId, Guid parentId, string name = "Point de Service")
        => Agency.Create(tenantId, name, "", AgencyType.ServicePoint, parentId, null);

    private static Agency Counter(Guid tenantId, Guid parentId, string name = "Guichet")
        => Agency.Create(tenantId, name, "", AgencyType.Counter, parentId, null);

    private GetAgencyTreeHandler BuildHandler()
        => new(db: _factory.CreateContext());

    // ── S1: tenant vide ──────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_empty_list_when_tenant_has_no_agencies()
    {
        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    // ── S2: racine unique sans enfants ────────────────────────────────────────

    [Fact]
    public async Task Returns_single_root_node_with_no_children()
    {
        await using var db = _factory.CreateContext();
        var hq = HQ(_tenantId);
        db.Agencies.Add(hq);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);

        var root = result.Value[0];
        root.Id.Should().Be(hq.Id);
        root.AgencyType.Should().Be(nameof(AgencyType.HeadQuarter));
        root.IsHeadQuarterAgency.Should().BeTrue();
        root.ParentAgencyId.Should().BeNull();
        root.ChildCount.Should().Be(0);
        root.Children.Should().BeEmpty();
    }

    // ── S3: arbre à deux niveaux ──────────────────────────────────────────────

    [Fact]
    public async Task Builds_two_level_tree()
    {
        await using var db = _factory.CreateContext();
        var hq = HQ(_tenantId);
        var branch = Branch(_tenantId, hq.Id);
        db.Agencies.AddRange(hq, branch);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);

        var root = result.Value[0];
        root.Id.Should().Be(hq.Id);
        root.ChildCount.Should().Be(1);
        root.Children.Should().HaveCount(1);

        var child = root.Children[0];
        child.Id.Should().Be(branch.Id);
        child.ParentAgencyId.Should().Be(hq.Id);
        child.AgencyType.Should().Be(nameof(AgencyType.Branch));
        child.ChildCount.Should().Be(0);
        child.Children.Should().BeEmpty();
    }

    // ── S4: arbre profond sur 4 niveaux ──────────────────────────────────────

    [Fact]
    public async Task Builds_four_level_deep_tree()
    {
        await using var db = _factory.CreateContext();
        var hq = HQ(_tenantId);
        var branch = Branch(_tenantId, hq.Id);
        var sp = ServicePoint(_tenantId, branch.Id);
        var counter = Counter(_tenantId, sp.Id);
        db.Agencies.AddRange(hq, branch, sp, counter);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var lvl1 = result.Value[0];
        var lvl2 = lvl1.Children[0];
        var lvl3 = lvl2.Children[0];
        var lvl4 = lvl3.Children[0];

        lvl1.Id.Should().Be(hq.Id);
        lvl2.Id.Should().Be(branch.Id);
        lvl3.Id.Should().Be(sp.Id);
        lvl4.Id.Should().Be(counter.Id);
        lvl4.Children.Should().BeEmpty();
    }

    // ── S5: plusieurs racines (forêt) ─────────────────────────────────────────

    [Fact]
    public async Task Returns_multiple_roots_ordered_by_name()
    {
        await using var db = _factory.CreateContext();
        var hq1 = HQ(_tenantId, "Siège Kaolack");
        var hq2 = HQ(_tenantId, "Siège Dakar");
        var hq3 = HQ(_tenantId, "Siège Ziguinchor");
        db.Agencies.AddRange(hq1, hq2, hq3);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value.Select(r => r.Name).Should().BeInAscendingOrder();
    }

    // ── S6: ChildCount correct à chaque niveau ────────────────────────────────

    [Fact]
    public async Task ChildCount_reflects_direct_children_only()
    {
        await using var db = _factory.CreateContext();
        var hq = HQ(_tenantId);
        var b1 = Branch(_tenantId, hq.Id, "Branche A");
        var b2 = Branch(_tenantId, hq.Id, "Branche B");
        var sp = ServicePoint(_tenantId, b1.Id);
        db.Agencies.AddRange(hq, b1, b2, sp);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null), CancellationToken.None);

        var root = result.Value[0];
        root.ChildCount.Should().Be(2);

        var brancheA = root.Children.First(c => c.Id == b1.Id);
        brancheA.ChildCount.Should().Be(1);

        var brancheB = root.Children.First(c => c.Id == b2.Id);
        brancheB.ChildCount.Should().Be(0);
    }

    // ── S7: sous-arbre via rootId ─────────────────────────────────────────────

    [Fact]
    public async Task Returns_subtree_rooted_at_given_agency()
    {
        await using var db = _factory.CreateContext();
        var hq = HQ(_tenantId);
        var branch = Branch(_tenantId, hq.Id);
        var sp = ServicePoint(_tenantId, branch.Id);
        db.Agencies.AddRange(hq, branch, sp);
        await db.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: branch.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);

        var root = result.Value[0];
        root.Id.Should().Be(branch.Id);
        root.ChildCount.Should().Be(1);
        root.Children[0].Id.Should().Be(sp.Id);
    }

    // ── S8: rootId inexistant ─────────────────────────────────────────────────

    [Fact]
    public async Task Returns_failure_when_rootId_not_found()
    {
        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // ── S9: les agences supprimées sont exclues par défaut ───────────────────

    [Fact]
    public async Task Excludes_deleted_agencies_by_default()
    {
        await using var db = _factory.CreateContext();
        var hq = HQ(_tenantId);
        var branch = Branch(_tenantId, hq.Id);
        db.Agencies.AddRange(hq, branch);
        await db.SaveChangesAsync();

        // soft-delete the branch — AsTracking() ensures mutations are detected by SaveChangesAsync
        await using var db2 = _factory.CreateContext();
        var toDelete = await db2.Agencies.AsTracking().FirstAsync(a => a.Id == branch.Id);
        toDelete.Deactivate();
        await db2.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null, IncludeDeleted: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Children.Should().BeEmpty();
    }

    // ── S10: includeDeleted = true ────────────────────────────────────────────

    [Fact]
    public async Task Includes_deleted_agencies_when_flag_is_true()
    {
        await using var db = _factory.CreateContext();
        var hq = HQ(_tenantId);
        var branch = Branch(_tenantId, hq.Id);
        db.Agencies.AddRange(hq, branch);
        await db.SaveChangesAsync();

        await using var db2 = _factory.CreateContext();
        var toDelete = await db2.Agencies.AsTracking().FirstAsync(a => a.Id == branch.Id);
        toDelete.Deactivate();
        await db2.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null, IncludeDeleted: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value[0].ChildCount.Should().Be(1);
        result.Value[0].Children[0].IsActive.Should().BeFalse();
    }

    // ── S11: isolation tenant ─────────────────────────────────────────────────

    [Fact]
    public async Task Does_not_return_agencies_from_another_tenant()
    {
        var otherTenantId = Guid.NewGuid();

        // Seed an agency for the current tenant
        await using var db = _factory.CreateContext();
        db.Agencies.Add(HQ(_tenantId, "Mon Siège"));
        await db.SaveChangesAsync();

        // Seed an agency for a different tenant using its own context/filter
        var otherFactory = new TestAdminDbContextFactory(otherTenantId);
        await using var otherDb = otherFactory.CreateContext();
        otherDb.Agencies.Add(HQ(otherTenantId, "Autre Siège"));
        await otherDb.SaveChangesAsync();

        var result = await BuildHandler().Handle(
            new GetAgencyTreeQuery(RootId: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Name.Should().Be("Mon Siège");

        otherFactory.Dispose();
    }
}
