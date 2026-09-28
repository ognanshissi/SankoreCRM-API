namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.AddGroupMember;
using Sankore.Modules.Customers.Features.Groups.CreateGroup;
using Sankore.Modules.Customers.Features.Groups.DissolveGroup;
using Sankore.Modules.Customers.Features.Groups.GetGroup;
using Sankore.Modules.Customers.Features.Groups.ListGroups;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

/// <summary>
/// Cross-tenant isolation for the whole Groups zone.
///
/// Both tenants share ONE store here (same InMemory database name, two different
/// <c>FixedTenantContext</c>s), which is the only arrangement in which the module's
/// global query filters are actually load-bearing: a handler that dropped its tenant
/// predicate would read tenant B's rows and these tests would go red.
/// </summary>
public sealed class GroupsTenantIsolationTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserA = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly string _database = $"groups-isolation-{Guid.NewGuid()}";

    private CustomersDbContext ContextFor(Guid tenantId) => GroupTestData.Context(_database, tenantId);

    /// <summary>A three-member group of tenant B, sharing tenant A's agency id on purpose.</summary>
    private async Task<(ClientGroup Group, List<Client> Clients)> SeedTenantBGroupAsync()
    {
        await using var seed = ContextFor(TenantB);
        var clients = await GroupTestData.SeedClientsAsync(seed, TenantB, AgencyId, 3, prefix: "B");

        var group = GroupTestData.GroupWith(
            TenantB, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
            [
                (clients[0].Id, GroupOfficeRole.President),
                (clients[1].Id, GroupOfficeRole.Treasurer),
                (clients[2].Id, GroupOfficeRole.Secretary),
            ],
            UserB,
            activateWithMinSize: 3);

        seed.ClientGroups.Add(group);
        await seed.SaveChangesAsync();
        seed.ChangeTracker.Clear();

        return (group, clients);
    }

    [Fact]
    public async Task A_group_of_another_tenant_is_invisible_to_a_detail_read()
    {
        var (group, _) = await SeedTenantBGroupAsync();

        var handler = new GetGroupHandler(
            ContextFor(TenantA),
            TestDoubles.CurrentUser(TenantA, UserA),
            // Unrestricted caller: only the tenant filter can hide the group.
            TestDoubles.AgencyScope(),
            new GroupSizePolicy(TestDoubles.Settings(TenantA)));

        var result = await handler.Handle(new GetGroupQuery(group.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task A_group_of_another_tenant_never_appears_in_a_listing()
    {
        await SeedTenantBGroupAsync();

        var handler = new ListGroupsHandler(
            ContextFor(TenantA),
            TestDoubles.CurrentUser(TenantA, UserA),
            TestDoubles.AgencyScope());

        var result = await handler.Handle(new ListGroupsQuery(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_group_name_taken_in_another_tenant_does_not_block_a_creation()
    {
        await SeedTenantBGroupAsync();

        var handler = new CreateGroupHandler(
            ContextFor(TenantA),
            TestDoubles.CurrentUser(TenantA, UserA),
            new RecordingGroupEventPublisher());

        // Same name, same agency id, different tenant: GROUP_NAME_ALREADY_USED is
        // scoped to (tenant, agency, name), so this must succeed.
        var result = await handler.Handle(
            new CreateGroupCommand(
                GroupType.SolidarityGroup, "Groupe Nimba", AgencyId, new DateOnly(2026, 1, 15)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_member_of_another_tenant_cannot_be_added_to_a_group_of_this_tenant()
    {
        var (_, tenantBClients) = await SeedTenantBGroupAsync();

        ClientGroup tenantAGroup;
        await using (var seed = ContextFor(TenantA))
        {
            tenantAGroup = TestClientFactory.Group(TenantA, AgencyId, name: "Groupe Yèlèn");
            seed.ClientGroups.Add(tenantAGroup);
            await seed.SaveChangesAsync();
        }

        var handler = new AddGroupMemberHandler(
            ContextFor(TenantA),
            TestDoubles.CurrentUser(TenantA, UserA),
            TestDoubles.AgencyScope(),
            new GroupSizePolicy(TestDoubles.Settings(TenantA)),
            new RecordingGroupEventPublisher());

        var result = await handler.Handle(
            new AddGroupMemberCommand(tenantAGroup.Id, tenantBClients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task A_mutation_cannot_reach_a_group_of_another_tenant()
    {
        var (group, _) = await SeedTenantBGroupAsync();

        var handler = new DissolveGroupHandler(
            ContextFor(TenantA),
            TestDoubles.CurrentUser(TenantA, UserA),
            TestDoubles.AgencyScope(),
            new RecordingGroupEventPublisher());

        var result = await handler.Handle(
            new DissolveGroupCommand(group.Id, "Attempted cross-tenant dissolution"),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.GroupNotFound);

        await using var assertions = ContextFor(TenantB);
        var stored = await assertions.ClientGroups.SingleAsync(g => g.Id == group.Id);
        stored.Status.Should().Be(GroupStatus.Active);
    }
}
