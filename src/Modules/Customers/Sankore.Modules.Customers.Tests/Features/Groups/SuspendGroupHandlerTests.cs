namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.SuspendGroup;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class SuspendGroupHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private SuspendGroupHandler BuildHandler(params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies));

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
    public async Task Suspends_the_group_and_keeps_every_membership_open()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler();

        var result = await handler.Handle(
            new SuspendGroupCommand(group.Id, "Repayment incident under review"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(GroupStatus.Suspended));
        result.Value.ActiveMemberCount.Should().Be(3);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.ClientGroups.SingleAsync(g => g.Id == group.Id);
        stored.Status.Should().Be(GroupStatus.Suspended);

        // A suspension is reversible, so nothing is closed.
        var memberships = await assertions.GroupMemberships
            .Where(m => m.GroupId == group.Id).ToListAsync();
        memberships.Should().AllSatisfy(m => m.LeftAt.Should().BeNull());
    }

    [Fact]
    public async Task Refuses_a_blank_motive_with_REASON_REQUIRED()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);
        var handler = BuildHandler();

        var result = await handler.Handle(
            new SuspendGroupCommand(group.Id, "  "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Refuses_to_suspend_a_group_twice()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);

        var first = await BuildHandler().Handle(
            new SuspendGroupCommand(group.Id, "Repayment incident"), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await BuildHandler().Handle(
            new SuspendGroupCommand(group.Id, "Repayment incident"), CancellationToken.None);

        second.Error.Should().Be(CustomerErrors.InvalidStatusTransition);
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_for_an_unknown_group()
    {
        var result = await BuildHandler().Handle(
            new SuspendGroupCommand(Guid.NewGuid(), "Repayment incident"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_for_a_group_outside_the_agency_perimeter()
    {
        var (group, _) = await SeedActiveGroupAsync(OtherAgencyId);

        var result = await BuildHandler(AgencyId).Handle(
            new SuspendGroupCommand(group.Id, "Repayment incident"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task Rejects_a_stale_expected_version_with_CONCURRENCY_CONFLICT()
    {
        var (group, _) = await SeedActiveGroupAsync(AgencyId);

        var result = await BuildHandler().Handle(
            new SuspendGroupCommand(group.Id, "Repayment incident", ExpectedVersion: 999u),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ConcurrencyConflict);
    }
}
