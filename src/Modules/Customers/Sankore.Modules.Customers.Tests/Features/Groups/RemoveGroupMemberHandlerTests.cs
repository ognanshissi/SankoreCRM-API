namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.RemoveGroupMember;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class RemoveGroupMemberHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private RemoveGroupMemberHandler BuildHandler(
        out RecordingGroupEventPublisher publisher,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingGroupEventPublisher();
        return new RemoveGroupMemberHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            new GroupSizePolicy(TestDoubles.Settings(TenantId)),
            publisher);
    }

    /// <summary>An Active solidarity group of three members holding the three offices.</summary>
    private async Task<(ClientGroup Group, List<Client> Clients)> SeedActiveGroupAsync(Guid agencyId)
    {
        await using var seed = _factory.CreateContext();
        var clients = await GroupTestData.SeedClientsAsync(seed, TenantId, agencyId, 3);

        var group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
            TenantId, agencyId, GroupType.SolidarityGroup, "Groupe Nimba",
            [
                (clients[0].Id, GroupOfficeRole.President),
                (clients[1].Id, GroupOfficeRole.Treasurer),
                (clients[2].Id, GroupOfficeRole.Secretary),
            ],
            UserId,
            activateWithMinSize: 3));

        group.Status.Should().Be(GroupStatus.Active);
        return (group, clients);
    }

    [Fact]
    public async Task Closes_the_membership_with_its_motive_instead_of_deleting_the_row()
    {
        var (group, clients) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[2].Id, "Moved to another region"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ActiveMemberCount.Should().Be(2);

        await using var assertions = _factory.CreateContext();
        var memberships = await assertions.GroupMemberships
            .Where(m => m.GroupId == group.Id).ToListAsync();

        // Three rows remain: nothing was ever physically deleted.
        memberships.Should().HaveCount(3);

        var closed = memberships.Single(m => m.ClientId == clients[2].Id);
        closed.LeftAt.Should().NotBeNull();
        closed.LeftAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        closed.LeaveReason.Should().Be("Moved to another region");
    }

    [Fact]
    public async Task Publishes_the_membership_change_with_the_role_the_client_held()
    {
        var (group, clients) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler(out var publisher);

        await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[0].Id, "Resigned"), CancellationToken.None);

        publisher.OfType<GroupMembershipChangedEvent>().Should().ContainSingle()
            .Which.Should().Match<GroupMembershipChangedEvent>(e =>
                e.GroupId == group.Id && e.ClientId == clients[0].Id
                && e.Change == "Left" && e.OfficeRole == nameof(GroupOfficeRole.President));
    }

    [Fact]
    public async Task Alerts_under_the_minimum_size_without_changing_the_group_status()
    {
        // group-min-size-solidarity = 3; removing one leaves two.
        var (group, clients) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[2].Id, "Left the village"),
            CancellationToken.None);

        result.Value.BelowMinimumSize.Should().BeTrue();
        result.Value.MinimumSize.Should().Be(3);
        result.Value.GroupStatus.Should().Be(nameof(GroupStatus.Active));

        publisher.OfType<ClientUnderMinimumGroupSizeEvent>().Should().ContainSingle()
            .Which.Should().Match<ClientUnderMinimumGroupSizeEvent>(e =>
                e.TenantId == TenantId && e.GroupId == group.Id
                && e.ActiveMembers == 2 && e.MinimumSize == 3);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.ClientGroups.SingleAsync(g => g.Id == group.Id);
        // The alert never demotes the group: only an operator decides.
        stored.Status.Should().Be(GroupStatus.Active);
    }

    [Fact]
    public async Task Does_not_alert_when_the_group_stays_at_or_above_the_minimum()
    {
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 4);
            group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [
                    (clients[0].Id, GroupOfficeRole.President),
                    (clients[1].Id, GroupOfficeRole.Treasurer),
                    (clients[2].Id, GroupOfficeRole.Secretary),
                    (clients[3].Id, GroupOfficeRole.Member),
                ],
                UserId,
                activateWithMinSize: 3));
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[3].Id, "Resigned"), CancellationToken.None);

        result.Value.BelowMinimumSize.Should().BeFalse();
        publisher.OfType<ClientUnderMinimumGroupSizeEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_alert_for_a_group_still_Forming()
    {
        // A Forming group is under its minimum by construction; alerting would be noise.
        List<Client> clients;
        ClientGroup group;

        await using (var seed = _factory.CreateContext())
        {
            clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 2);
            group = await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
                TenantId, AgencyId, GroupType.SolidarityGroup, "Groupe Nimba",
                [(clients[0].Id, GroupOfficeRole.President), (clients[1].Id, GroupOfficeRole.Treasurer)],
                UserId));
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[1].Id, "Resigned"), CancellationToken.None);

        result.Value.BelowMinimumSize.Should().BeFalse();
        publisher.OfType<ClientUnderMinimumGroupSizeEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_a_blank_motive_with_REASON_REQUIRED()
    {
        var (group, clients) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[0].Id, "   "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Reports_MEMBERSHIP_NOT_FOUND_when_the_client_is_not_an_active_member()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, Guid.NewGuid(), "Resigned"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.MembershipNotFound);
    }

    [Fact]
    public async Task Reports_MEMBERSHIP_NOT_FOUND_on_a_second_removal_of_the_same_client()
    {
        var (group, clients) = await SeedActiveGroupAsync(AgencyId);

        var first = await BuildHandler(out _).Handle(
            new RemoveGroupMemberCommand(group.Id, clients[0].Id, "Resigned"), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await BuildHandler(out _).Handle(
            new RemoveGroupMemberCommand(group.Id, clients[0].Id, "Resigned"), CancellationToken.None);

        second.Error.Should().Be(CustomerErrors.MembershipNotFound);
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_for_a_group_outside_the_agency_perimeter()
    {
        var (group, clients) = await SeedActiveGroupAsync(OtherAgencyId);
        var handler = BuildHandler(out _, AgencyId);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[0].Id, "Resigned"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task Rejects_a_stale_expected_version_with_CONCURRENCY_CONFLICT()
    {
        var (group, clients) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new RemoveGroupMemberCommand(group.Id, clients[0].Id, "Resigned", ExpectedVersion: 999u),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);
    }
}
