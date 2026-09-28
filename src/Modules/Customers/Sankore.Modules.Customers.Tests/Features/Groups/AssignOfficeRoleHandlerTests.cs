namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.AssignOfficeRole;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class AssignOfficeRoleHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private AssignOfficeRoleHandler BuildHandler(
        out RecordingGroupEventPublisher publisher,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingGroupEventPublisher();
        return new AssignOfficeRoleHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            new GroupSizePolicy(TestDoubles.Settings(TenantId)),
            publisher);
    }

    private async Task<(ClientGroup Group, List<Client> Clients)> SeedFormingGroupAsync(
        Guid agencyId, params GroupOfficeRole[] roles)
    {
        await using var seed = _factory.CreateContext();
        var clients = await GroupTestData.SeedClientsAsync(seed, TenantId, agencyId, roles.Length);

        var group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
            TenantId, agencyId, GroupType.SolidarityGroup, "Groupe Nimba",
            roles.Select((role, i) => (clients[i].Id, role)),
            UserId));

        return (group, clients);
    }

    [Fact]
    public async Task Promotes_the_member_and_demotes_the_previous_holder_to_Member()
    {
        var (group, clients) = await SeedFormingGroupAsync(
            AgencyId, GroupOfficeRole.President, GroupOfficeRole.Member);

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(group.Id, clients[1].Id, GroupOfficeRole.President),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OfficeRole.Should().Be(nameof(GroupOfficeRole.President));
        result.Value.PreviousHolderClientId.Should().Be(clients[0].Id);

        publisher.OfType<GroupMembershipChangedEvent>().Should().ContainSingle()
            .Which.Change.Should().Be("RoleChanged");

        await using var assertions = _factory.CreateContext();
        var memberships = await assertions.GroupMemberships
            .Where(m => m.GroupId == group.Id).ToListAsync();

        // Exactly one President at any point in time.
        memberships.Count(m => m.LeftAt == null && m.OfficeRole == GroupOfficeRole.President)
            .Should().Be(1);
        memberships.Single(m => m.ClientId == clients[0].Id).OfficeRole
            .Should().Be(GroupOfficeRole.Member);
        memberships.Single(m => m.ClientId == clients[1].Id).OfficeRole
            .Should().Be(GroupOfficeRole.President);
    }

    [Fact]
    public async Task Reports_no_previous_holder_when_the_office_was_vacant()
    {
        var (group, clients) = await SeedFormingGroupAsync(AgencyId, GroupOfficeRole.Member);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(group.Id, clients[0].Id, GroupOfficeRole.Treasurer),
            CancellationToken.None);

        result.Value.PreviousHolderClientId.Should().BeNull();
        result.Value.OfficeRole.Should().Be(nameof(GroupOfficeRole.Treasurer));
    }

    [Fact]
    public async Task Activates_the_group_when_the_role_change_fills_the_last_vacant_office()
    {
        // Three members (min size for a solidarity group) but no Secretary yet.
        var (group, clients) = await SeedFormingGroupAsync(
            AgencyId, GroupOfficeRole.President, GroupOfficeRole.Treasurer, GroupOfficeRole.Member);

        group.Status.Should().Be(GroupStatus.Forming);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(group.Id, clients[2].Id, GroupOfficeRole.Secretary),
            CancellationToken.None);

        result.Value.Activated.Should().BeTrue();
        result.Value.GroupStatus.Should().Be(nameof(GroupStatus.Active));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.ClientGroups.SingleAsync(g => g.Id == group.Id);
        stored.Status.Should().Be(GroupStatus.Active);
    }

    [Fact]
    public async Task Demoting_an_office_holder_to_Member_is_allowed_and_leaves_the_office_vacant()
    {
        var (group, clients) = await SeedFormingGroupAsync(AgencyId, GroupOfficeRole.President);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(group.Id, clients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PreviousHolderClientId.Should().BeNull();

        await using var assertions = _factory.CreateContext();
        var memberships = await assertions.GroupMemberships
            .Where(m => m.GroupId == group.Id).ToListAsync();
        memberships.Should().AllSatisfy(m => m.OfficeRole.Should().Be(GroupOfficeRole.Member));
    }

    [Fact]
    public async Task Reports_MEMBERSHIP_NOT_FOUND_when_the_client_is_not_an_active_member()
    {
        var (group, _) = await SeedFormingGroupAsync(AgencyId, GroupOfficeRole.President);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(group.Id, Guid.NewGuid(), GroupOfficeRole.Treasurer),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.MembershipNotFound);
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_for_a_group_outside_the_agency_perimeter()
    {
        var (group, clients) = await SeedFormingGroupAsync(OtherAgencyId, GroupOfficeRole.Member);

        var handler = BuildHandler(out _, AgencyId);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(group.Id, clients[0].Id, GroupOfficeRole.President),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task Rejects_a_stale_expected_version_with_CONCURRENCY_CONFLICT()
    {
        var (group, clients) = await SeedFormingGroupAsync(AgencyId, GroupOfficeRole.Member);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(
                group.Id, clients[0].Id, GroupOfficeRole.President, ExpectedVersion: 999u),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);
    }

    [Fact]
    public async Task Refuses_a_role_change_on_a_dissolved_group()
    {
        var (group, clients) = await SeedFormingGroupAsync(AgencyId, GroupOfficeRole.Member);

        await using (var mutate = _factory.CreateContext())
        {
            var tracked = await mutate.ClientGroups.AsTracking()
                .Include(g => g.Memberships)
                .SingleAsync(g => g.Id == group.Id);
            tracked.Dissolve("End of cycle", DateTimeOffset.UtcNow, UserId);
            await mutate.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AssignOfficeRoleCommand(group.Id, clients[0].Id, GroupOfficeRole.President),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.InvalidStatusTransition);
    }
}
