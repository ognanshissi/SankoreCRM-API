namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.AddGroupMember;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class AddGroupMemberHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private AddGroupMemberHandler BuildHandler(
        out RecordingGroupEventPublisher publisher,
        ICustomerSettings? settings = null,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingGroupEventPublisher();
        return new AddGroupMemberHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            new GroupSizePolicy(settings ?? TestDoubles.Settings(TenantId)),
            publisher);
    }

    [Fact]
    public async Task Adds_an_eligible_client_and_publishes_GroupMembershipChangedEvent()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 1);
            group = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId));
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[0].Id, GroupOfficeRole.President),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.OfficeRole.Should().Be(nameof(GroupOfficeRole.President));
        result.Value.ActiveMemberCount.Should().Be(1);
        result.Value.GroupStatus.Should().Be(nameof(GroupStatus.Forming));
        result.Value.Activated.Should().BeFalse();

        publisher.OfType<GroupMembershipChangedEvent>().Should().ContainSingle()
            .Which.Should().Match<GroupMembershipChangedEvent>(e =>
                e.TenantId == TenantId && e.GroupId == group.Id
                && e.ClientId == clients[0].Id && e.Change == "Joined"
                && e.OfficeRole == nameof(GroupOfficeRole.President));

        await using var assertions = _factory.CreateContext();
        var membership = await assertions.GroupMemberships.SingleAsync(m => m.GroupId == group.Id);
        membership.ClientId.Should().Be(clients[0].Id);
        membership.LeftAt.Should().BeNull();
        membership.CreatedBy.Should().Be(UserId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refuses_a_client_that_is_archived_or_KYC_rejected_with_GROUP_MEMBER_NOT_ELIGIBLE(bool archived)
    {
        ClientGroup group;
        Client ineligible;

        await using (var seed = _factory.CreateContext())
        {
            ineligible = archived
                ? GroupTestData.ArchivedClient(TenantId, AgencyId, UserId)
                : GroupTestData.KycRejectedClient(TenantId, AgencyId, UserId);

            await TestClientFactory.SeedAsync(seed, ineligible);
            group = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId));
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, ineligible.Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.GroupMemberNotEligible);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_a_join_once_the_type_maximum_size_is_reached()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 3);
            group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [(clients[0].Id, GroupOfficeRole.President), (clients[1].Id, GroupOfficeRole.Treasurer)],
                UserId));
        }

        // Tenant lowers the ceiling to two members for solidarity groups.
        var settings = TestDoubles.Settings(TenantId, (CustomerSettingKeys.GroupMaxSizeSolidarity, "2"));
        var handler = BuildHandler(out _, settings);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[2].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.GroupSizeLimitReached);
    }

    [Fact]
    public async Task Refuses_a_second_active_solidarity_group_when_the_tenant_rule_is_on()
    {
        List<Client> clients;
        ClientGroup second;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 1);

            await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [(clients[0].Id, GroupOfficeRole.Member)], UserId));

            second = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId, name: "Groupe Yèlèn"));
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AddGroupMemberCommand(second.Id, clients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.AlreadyInSolidarityGroup);
    }

    [Fact]
    public async Task Allows_a_second_solidarity_group_when_the_tenant_rule_is_off()
    {
        List<Client> clients;
        ClientGroup second;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 1);

            await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [(clients[0].Id, GroupOfficeRole.Member)], UserId));

            second = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId, name: "Groupe Yèlèn"));
        }

        var settings = TestDoubles.Settings(TenantId, (CustomerSettingKeys.SolidaritySingleGroupRule, "false"));
        var handler = BuildHandler(out _, settings);

        var result = await handler.Handle(
            new AddGroupMemberCommand(second.Id, clients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Does_not_apply_the_single_group_rule_to_tontines()
    {
        List<Client> clients;
        ClientGroup second;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 1);

            await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.Tontine, "Tontine Wari",
                [(clients[0].Id, GroupOfficeRole.Member)], UserId));

            second = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId, GroupType.Tontine, "Tontine Djè"));
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AddGroupMemberCommand(second.Id, clients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Activates_the_group_once_the_minimum_size_and_the_three_offices_are_reached()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 3);
            group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [(clients[0].Id, GroupOfficeRole.President), (clients[1].Id, GroupOfficeRole.Treasurer)],
                UserId));
        }

        // group-min-size-solidarity = 3: this third member is also the missing Secretary.
        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[2].Id, GroupOfficeRole.Secretary),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Activated.Should().BeTrue();
        result.Value.GroupStatus.Should().Be(nameof(GroupStatus.Active));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.ClientGroups.SingleAsync(g => g.Id == group.Id);
        stored.Status.Should().Be(GroupStatus.Active);
    }

    [Fact]
    public async Task Stays_Forming_when_the_minimum_size_is_reached_but_an_office_is_vacant()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 3);
            group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [(clients[0].Id, GroupOfficeRole.President), (clients[1].Id, GroupOfficeRole.Treasurer)],
                UserId));
        }

        var handler = BuildHandler(out _);

        // Third member joins as a plain member: no Secretary, so no activation.
        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[2].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.Value.Activated.Should().BeFalse();
        result.Value.GroupStatus.Should().Be(nameof(GroupStatus.Forming));
    }

    [Fact]
    public async Task Stays_Forming_when_the_three_offices_are_filled_but_the_minimum_size_is_not_reached()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 3);
            group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.Vsla, "AVEC Sikasso",
                [(clients[0].Id, GroupOfficeRole.President), (clients[1].Id, GroupOfficeRole.Treasurer)],
                UserId));
        }

        // group-min-size-vsla = 15: three members with three offices is not enough.
        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[2].Id, GroupOfficeRole.Secretary),
            CancellationToken.None);

        result.Value.Activated.Should().BeFalse();
        result.Value.GroupStatus.Should().Be(nameof(GroupStatus.Forming));
    }

    [Fact]
    public async Task Is_idempotent_when_the_client_is_already_an_active_member()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 1);
            group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [(clients[0].Id, GroupOfficeRole.President)], UserId));
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // The existing membership is returned untouched — the role is not changed here.
        result.Value.OfficeRole.Should().Be(nameof(GroupOfficeRole.President));
        publisher.Published.Should().BeEmpty();

        await using var assertions = _factory.CreateContext();
        var count = await assertions.GroupMemberships.CountAsync(m => m.GroupId == group.Id);
        count.Should().Be(1);
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_for_a_group_outside_the_agency_perimeter()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, OtherAgencyId, 1);
            group = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, OtherAgencyId));
        }

        // Caller only sees AgencyId; the group belongs to OtherAgencyId.
        var handler = BuildHandler(out _, settings: null, accessibleAgencies: AgencyId);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_for_an_unknown_client()
    {
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
            group = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId));

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, Guid.NewGuid(), GroupOfficeRole.Member),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Rejects_a_stale_expected_version_with_CONCURRENCY_CONFLICT()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 1);
            group = await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId));
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AddGroupMemberCommand(
                group.Id, clients[0].Id, GroupOfficeRole.Member, ExpectedVersion: 999u),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);
    }

    [Fact]
    public async Task Refuses_to_add_a_member_to_a_dissolved_group()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 1);
            var built = TestClientFactory.Group(TenantId, AgencyId);
            built.Dissolve("End of cycle", DateTimeOffset.UtcNow, UserId);
            group = await TestClientFactory.SeedGroupAsync(seed, built);
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new AddGroupMemberCommand(group.Id, clients[0].Id, GroupOfficeRole.Member),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.InvalidStatusTransition);
    }
}
