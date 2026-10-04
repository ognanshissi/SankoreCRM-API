namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The lead-dispatch pool (<c>IAdministrationModule.GetAvailableAgentsAsync</c>).
///
/// <para>
/// It threw <c>NotImplementedException</c> until now, which surfaced as a 500 on every dispatch.
/// What is pinned here is the shape of "available", because every condition in it is a way to park
/// a lead on somebody who will never see it while the SLA clock runs: a never-activated account, a
/// disabled one, an agent who turned availability off, a revoked role grant, a technical account
/// that happens to hold an agent role, and a user with no agency at all — whose summary would
/// otherwise claim agency <c>Guid.Empty</c> to the geographic scorer.
/// </para>
/// </summary>
public sealed class GetAvailableAgentsTests : IDisposable
{
    private static readonly Guid BranchA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid BranchB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly Dictionary<string, Guid> _roleIds = new(StringComparer.OrdinalIgnoreCase);

    public GetAvailableAgentsTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        SeedRoles();
    }

    public void Dispose() => _factory.Dispose();

    private void SeedRoles()
    {
        using var db = _factory.CreateContext();

        foreach (var role in Roles.All)
        {
            var entity = AppRole.Create(role.Code, role.Name);
            db.Roles.Add(entity);
            _roleIds[role.Code] = entity.Id;
        }

        db.SaveChanges();
    }

    private AdministrationModuleFacade Facade() => new(_factory.CreateContext());

    /// <summary>Creates an agent and grants it a role through the tenant-scoped mirror.</summary>
    private AppUser SeedAgent(
        string name,
        Guid? agencyId = null,
        string? role = null,
        bool active = true,
        bool available = true,
        bool roleRevoked = false,
        Guid? tenantId = null,
        bool superUser = false)
    {
        var tenant = tenantId ?? _tenantId;

        var user = superUser
            ? AppUser.CreateRoot(tenant, name, "Root", $"{name}@example.test")
            : AppUser.CreateAgent(
                tenant, agencyId ?? BranchA, name, "Agent", $"{name}@example.test",
                ["fr"], ["Loan"]);

        if (active) user.Activate();
        if (!available) user.SetAvailability(false);

        using var db = _factory.CreateContext();
        db.Users.Add(user);

        if (role is not null)
        {
            var grant = UserRole.Assign(tenant, user.Id, _roleIds[role], Guid.NewGuid());
            if (roleRevoked) grant.Revoke();
            db.UserRoles.Add(grant);
        }

        db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task A_commercial_agent_of_the_agency_is_returned_with_its_dispatch_signals()
    {
        var agent = SeedAgent("Awa", BranchA, Roles.CommercialAgent.Code);

        var pool = await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default);

        pool.Should().HaveCount(1);
        var summary = pool[0];
        summary.Id.Should().Be(agent.Id);
        summary.AgencyId.Should().Be(BranchA);
        summary.IsAvailable.Should().BeTrue();
        summary.SpokenLanguages.Should().Contain("fr");
        summary.Specialties.Should().Contain("Loan");
    }

    [Fact]
    public async Task The_plain_Agent_role_is_in_the_pool_too()
    {
        // Both roles are seeded and nothing distinguishes them; depending on which one an
        // administrator picked would mean NO_AGENT_AVAILABLE on every lead of half the tenants.
        SeedAgent("Kofi", BranchA, Roles.Agent.Code);

        (await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_role_outside_the_pool_is_not_an_agent()
    {
        SeedAgent("Caissier", BranchA, Roles.Cashier.Code);

        (await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_with_no_role_grant_at_all_is_not_an_agent()
    {
        SeedAgent("SansRole", BranchA, role: null);

        (await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_revoked_grant_stops_receiving_leads()
    {
        // The revoke is a soft one on the mirror row. Reading Identity's join table instead would
        // keep this agent in the pool for good.
        SeedAgent("Revoque", BranchA, Roles.CommercialAgent.Code, roleRevoked: true);

        (await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_agent_who_turned_availability_off_is_excluded()
    {
        SeedAgent("Absente", BranchA, Roles.CommercialAgent.Code, available: false);

        (await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_account_that_has_never_been_activated_is_excluded()
    {
        // PendingActivation: the invitation may still be unread. The lead would sit there with its
        // SLA running.
        SeedAgent("Invite", BranchA, Roles.CommercialAgent.Code, active: false);

        (await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_super_user_is_never_in_the_pool()
    {
        SeedAgent("Root", role: Roles.CommercialAgent.Code, superUser: true);

        (await Facade().GetAvailableAgentsAsync(_tenantId, null, default)).Should().BeEmpty(
            "a technical account is not a commercial team, whatever role it was granted");
    }

    [Fact]
    public async Task The_agency_filter_matches_exactly_and_not_the_whole_tenant()
    {
        var mine = SeedAgent("Ama", BranchA, Roles.CommercialAgent.Code);
        SeedAgent("Yao", BranchB, Roles.CommercialAgent.Code);

        var pool = await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default);

        pool.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    [Fact]
    public async Task No_preferred_agency_means_every_agency_of_the_tenant()
    {
        SeedAgent("Ama", BranchA, Roles.CommercialAgent.Code);
        SeedAgent("Yao", BranchB, Roles.CommercialAgent.Code);

        (await Facade().GetAvailableAgentsAsync(_tenantId, null, default)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Another_tenants_agent_is_never_returned()
    {
        var otherTenant = Guid.NewGuid();
        SeedAgent("Etranger", BranchA, Roles.CommercialAgent.Code, tenantId: otherTenant);

        (await Facade().GetAvailableAgentsAsync(_tenantId, null, default)).Should().BeEmpty();
        (await Facade().GetAvailableAgentsAsync(otherTenant, null, default)).Should().HaveCount(1,
            "the read is explicit about its tenant, so it must still work for the other one");
    }

    [Fact]
    public async Task The_pool_is_ordered_the_same_way_twice()
    {
        // The strategies rank on load and conversion rate, where ties are the norm. An unordered
        // pool makes a tie resolve differently on each dispatch.
        for (var i = 0; i < 5; i++)
            SeedAgent($"Agent{i}", BranchA, Roles.CommercialAgent.Code);

        var first = await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default);
        var second = await Facade().GetAvailableAgentsAsync(_tenantId, BranchA, default);

        first.Select(a => a.Id).Should().Equal(second.Select(a => a.Id));
    }
}
