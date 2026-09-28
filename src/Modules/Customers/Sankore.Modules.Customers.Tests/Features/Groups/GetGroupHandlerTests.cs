namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.GetGroup;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class GetGroupHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private GetGroupHandler BuildHandler(params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            new GroupSizePolicy(TestDoubles.Settings(TenantId)));

    private async Task<(ClientGroup Group, List<Client> Clients)> SeedAsync(Guid agencyId)
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
    public async Task Returns_the_group_with_its_active_members_and_office_holders()
    {
        var (group, clients) = await SeedAsync(AgencyId);

        var result = await BuildHandler().Handle(new GetGroupQuery(group.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;

        dto.Name.Should().Be("Groupe Nimba");
        dto.Status.Should().Be(nameof(GroupStatus.Active));
        dto.Type.Should().Be(nameof(GroupType.SolidarityGroup));
        dto.ActiveMemberCount.Should().Be(3);
        dto.Members.Should().HaveCount(3);
        dto.PresidentClientId.Should().Be(clients[0].Id);
        dto.TreasurerClientId.Should().Be(clients[1].Id);
        dto.SecretaryClientId.Should().Be(clients[2].Id);
    }

    [Fact]
    public async Task Exposes_the_tenant_size_envelope_of_the_group_type()
    {
        var (group, _) = await SeedAsync(AgencyId);

        var result = await BuildHandler().Handle(new GetGroupQuery(group.Id), CancellationToken.None);

        // group-min-size-solidarity = 3, group-max-size-solidarity = 30.
        result.Value.MinimumSize.Should().Be(3);
        result.Value.MaximumSize.Should().Be(30);
    }

    [Fact]
    public async Task Carries_the_client_number_and_display_name_of_every_member()
    {
        var (group, clients) = await SeedAsync(AgencyId);

        var result = await BuildHandler().Handle(new GetGroupQuery(group.Id), CancellationToken.None);

        var member = result.Value.Members.Single(m => m.ClientId == clients[0].Id);
        member.ClientNumber.Should().Be(clients[0].ClientNumber);
        member.DisplayName.Should().Be(clients[0].DisplayName);
        member.ClientStatus.Should().Be(nameof(ClientStatus.PendingKyc));
        member.LeftAt.Should().BeNull();
    }

    [Fact]
    public async Task Hides_closed_memberships_unless_they_are_asked_for()
    {
        var (group, clients) = await SeedAsync(AgencyId);

        await using (var mutate = _factory.CreateContext())
        {
            var tracked = await mutate.ClientGroups.AsTracking()
                .Include(g => g.Memberships)
                .SingleAsync(g => g.Id == group.Id);
            tracked.RemoveMember(clients[2].Id, "Moved away", DateTimeOffset.UtcNow, UserId);
            await mutate.SaveChangesAsync();
        }

        var activeOnly = await BuildHandler().Handle(new GetGroupQuery(group.Id), CancellationToken.None);
        activeOnly.Value.Members.Should().HaveCount(2);
        activeOnly.Value.ActiveMemberCount.Should().Be(2);
        activeOnly.Value.SecretaryClientId.Should().BeNull();

        var withFormer = await BuildHandler()
            .Handle(new GetGroupQuery(group.Id, IncludeFormerMembers: true), CancellationToken.None);

        withFormer.Value.Members.Should().HaveCount(3);
        withFormer.Value.ActiveMemberCount.Should().Be(2);

        var former = withFormer.Value.Members.Single(m => m.ClientId == clients[2].Id);
        former.LeftAt.Should().NotBeNull();
        former.LeaveReason.Should().Be("Moved away");
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_for_an_unknown_group()
    {
        var result = await BuildHandler().Handle(new GetGroupQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }

    [Fact]
    public async Task Reports_GROUP_NOT_FOUND_rather_than_a_403_for_a_group_outside_the_perimeter()
    {
        var (group, _) = await SeedAsync(OtherAgencyId);

        var result = await BuildHandler(AgencyId).Handle(new GetGroupQuery(group.Id), CancellationToken.None);

        // Never AGENCY_OUT_OF_SCOPE: a 403 would confirm the group exists.
        result.Error.Should().Be(CustomerErrors.GroupNotFound);
    }
}
