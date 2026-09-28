namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.DissolveGroup;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class DissolveGroupHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private DissolveGroupHandler BuildHandler(
        out RecordingGroupEventPublisher publisher,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingGroupEventPublisher();
        return new DissolveGroupHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            publisher);
    }

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

        return (group, clients);
    }

    [Fact]
    public async Task Dissolves_the_group_and_closes_every_active_membership()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new DissolveGroupCommand(group.Id, "End of the savings cycle"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(GroupStatus.Dissolved));
        result.Value.ClosedMemberships.Should().Be(3);
        result.Value.DissolvedAt.Should().NotBeNull();

        publisher.OfType<GroupDissolvedEvent>().Should().ContainSingle()
            .Which.Should().Match<GroupDissolvedEvent>(e =>
                e.TenantId == TenantId && e.GroupId == group.Id
                && e.Reason == "End of the savings cycle");

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.ClientGroups.SingleAsync(g => g.Id == group.Id);
        stored.Status.Should().Be(GroupStatus.Dissolved);
        stored.DissolutionReason.Should().Be("End of the savings cycle");
        stored.DissolvedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        var memberships = await assertions.GroupMemberships
            .Where(m => m.GroupId == group.Id).ToListAsync();

        // Closed, not deleted: the group's composition stays reconstructible.
        memberships.Should().HaveCount(3);
        memberships.Should().AllSatisfy(m =>
        {
            m.LeftAt.Should().NotBeNull();
            m.LeaveReason.Should().Be("End of the savings cycle");
        });
    }

    [Fact]
    public async Task Leaves_already_closed_memberships_untouched()
    {
        var (group, clients) = await SeedActiveGroupAsync(AgencyId);

        await using (var mutate = _factory.CreateContext())
        {
            var tracked = await mutate.ClientGroups.AsTracking()
                .Include(g => g.Memberships)
                .SingleAsync(g => g.Id == group.Id);
            tracked.RemoveMember(clients[0].Id, "Resigned earlier", DateTimeOffset.UtcNow, UserId);
            await mutate.SaveChangesAsync();
        }

        var result = await BuildHandler(out _).Handle(
            new DissolveGroupCommand(group.Id, "End of the savings cycle"), CancellationToken.None);

        result.Value.ClosedMemberships.Should().Be(2);

        await using var assertions = _factory.CreateContext();
        var earlier = await assertions.GroupMemberships
            .SingleAsync(m => m.GroupId == group.Id && m.ClientId == clients[0].Id);
        earlier.LeaveReason.Should().Be("Resigned earlier");
    }

    [Fact]
    public async Task Refuses_a_blank_motive_with_REASON_REQUIRED()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);

        var result = await BuildHandler(out var publisher).Handle(
            new DissolveGroupCommand(group.Id, "   "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_to_dissolve_a_group_twice()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);

        var first = await BuildHandler(out _).Handle(
            new DissolveGroupCommand(group.Id, "End of cycle"), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await BuildHandler(out _).Handle(
            new DissolveGroupCommand(group.Id, "End of cycle"), CancellationToken.None);

        second.Error.Should().Be(CustomerErrors.InvalidStatusTransition);
    }

    [Fact]
    public async Task Dissolves_a_suspended_group_as_well()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);

        await using (var mutate = _factory.CreateContext())
        {
            var tracked = await mutate.ClientGroups.AsTracking().SingleAsync(g => g.Id == group.Id);
            tracked.Suspend("Repayment incident", UserId);
            await mutate.SaveChangesAsync();
        }

        var result = await BuildHandler(out _).Handle(
            new DissolveGroupCommand(group.Id, "Unrecoverable"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(GroupStatus.Dissolved));
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_for_a_group_outside_the_agency_perimeter()
    {
        var (group, _) = await SeedActiveGroupAsync(OtherAgencyId);

        var result = await BuildHandler(out _, AgencyId).Handle(
            new DissolveGroupCommand(group.Id, "End of cycle"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task Rejects_a_stale_expected_version_with_CONCURRENCY_CONFLICT()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);

        var result = await BuildHandler(out _).Handle(
            new DissolveGroupCommand(group.Id, "End of cycle", ExpectedVersion: 999u),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);
    }
}
