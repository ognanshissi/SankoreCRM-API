namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.ListGroups;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class ListGroupsHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private ListGroupsHandler BuildHandler(params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies));

    private async Task SeedAsync()
    {
        await using var seed = _factory.CreateContext();
        var clients = await GroupTestData.SeedClientsAsync(seed, TenantId, AgencyId, 2);

        await TestClientFactory.SeedGroupAsync(seed, GroupTestData.GroupWith(
            TenantId, AgencyId, GroupType.SolidarityGroup, "Alpha Nimba",
            [(clients[0].Id, GroupOfficeRole.President), (clients[1].Id, GroupOfficeRole.Treasurer)],
            UserId));

        await TestClientFactory.SeedGroupAsync(
            seed, TestClientFactory.Group(TenantId, AgencyId, GroupType.Tontine, "Beta Wari"));

        var dissolved = TestClientFactory.Group(TenantId, AgencyId, GroupType.Vsla, "Gamma Sikasso");
        dissolved.Dissolve("End of cycle", DateTimeOffset.UtcNow, UserId);
        await TestClientFactory.SeedGroupAsync(seed, dissolved);

        await TestClientFactory.SeedGroupAsync(
            seed, TestClientFactory.Group(TenantId, OtherAgencyId, GroupType.Tontine, "Delta Korhogo"));
    }

    [Fact]
    public async Task Lists_every_group_of_the_tenant_ordered_by_name_for_an_unrestricted_caller()
    {
        await SeedAsync();

        var result = await BuildHandler().Handle(new ListGroupsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(4);
        result.Value.Items.Select(i => i.Name)
            .Should().ContainInOrder("Alpha Nimba", "Beta Wari", "Delta Korhogo", "Gamma Sikasso");
    }

    [Fact]
    public async Task Counts_only_the_active_members_of_each_group()
    {
        await SeedAsync();

        var result = await BuildHandler().Handle(new ListGroupsQuery(), CancellationToken.None);

        result.Value.Items.Single(i => i.Name == "Alpha Nimba").ActiveMemberCount.Should().Be(2);
        result.Value.Items.Single(i => i.Name == "Beta Wari").ActiveMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Restricts_the_listing_to_the_accessible_agencies()
    {
        await SeedAsync();

        var result = await BuildHandler(AgencyId).Handle(new ListGroupsQuery(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(3);
        result.Value.Items.Should().NotContain(i => i.Name == "Delta Korhogo");
    }

    [Fact]
    public async Task Returns_an_empty_page_for_an_agency_filter_outside_the_perimeter()
    {
        await SeedAsync();

        var result = await BuildHandler(AgencyId)
            .Handle(new ListGroupsQuery(AgencyId: OtherAgencyId), CancellationToken.None);

        // An empty page, not a 403: the filter is intersected with the perimeter.
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Filters_by_type_and_by_status()
    {
        await SeedAsync();

        var tontines = await BuildHandler()
            .Handle(new ListGroupsQuery(Type: GroupType.Tontine), CancellationToken.None);
        tontines.Value.Items.Should().OnlyContain(i => i.Type == nameof(GroupType.Tontine));
        tontines.Value.TotalCount.Should().Be(2);

        var dissolved = await BuildHandler()
            .Handle(new ListGroupsQuery(Status: GroupStatus.Dissolved), CancellationToken.None);
        dissolved.Value.Items.Should().ContainSingle()
            .Which.Name.Should().Be("Gamma Sikasso");
    }

    [Fact]
    public async Task Filters_by_name_substring_case_insensitively()
    {
        await SeedAsync();

        var result = await BuildHandler()
            .Handle(new ListGroupsQuery(Search: "wari"), CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.Name.Should().Be("Beta Wari");
    }

    [Fact]
    public async Task Paginates_and_clamps_a_page_below_one()
    {
        await SeedAsync();

        var page = await BuildHandler()
            .Handle(new ListGroupsQuery(Page: 2, PageSize: 2), CancellationToken.None);

        page.Value.Page.Should().Be(2);
        page.Value.Items.Should().HaveCount(2);
        page.Value.Items.Select(i => i.Name).Should().ContainInOrder("Delta Korhogo", "Gamma Sikasso");

        var clamped = await BuildHandler()
            .Handle(new ListGroupsQuery(Page: -5, PageSize: 0), CancellationToken.None);

        clamped.Value.Page.Should().Be(1);
        clamped.Value.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task Returns_nothing_when_the_caller_sees_no_agency_at_all()
    {
        await SeedAsync();

        // An empty accessible set is the opposite of null: the user exists but sees nothing.
        var result = await BuildHandler(Guid.NewGuid()).Handle(new ListGroupsQuery(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(0);
    }
}
