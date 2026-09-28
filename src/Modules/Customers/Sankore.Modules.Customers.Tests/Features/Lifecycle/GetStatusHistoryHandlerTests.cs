namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.GetStatusHistory;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class GetStatusHistoryHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private GetStatusHistoryHandler BuildHandler(params Guid[] accessibleAgencies) => new(
        _factory.CreateContext(),
        TestDoubles.CurrentUser(TenantId, UserId),
        TestDoubles.AgencyScope(accessibleAgencies));

    private async Task SeedAsync(Client client)
    {
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Returns_the_transitions_most_recent_first()
    {
        // Creation -> PendingKyc, KYC approval -> Active, suspension -> Suspended.
        var client = ClientBuilder.Suspended(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var result = await BuildHandler().Handle(
            new GetStatusHistoryQuery(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3);
        result.Value.Items.Select(i => i.NewStatus).Should().ContainInOrder(
            nameof(ClientStatus.Suspended),
            nameof(ClientStatus.Active),
            nameof(ClientStatus.PendingKyc));
    }

    [Fact]
    public async Task Exposes_the_motive_the_actor_and_a_null_previous_status_on_the_first_line()
    {
        var client = ClientBuilder.Suspended(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var result = await BuildHandler().Handle(
            new GetStatusHistoryQuery(client.Id), CancellationToken.None);

        var creation = result.Value.Items.Single(i => i.NewStatus == nameof(ClientStatus.PendingKyc));
        creation.OldStatus.Should().BeNull();
        creation.Reason.Should().BeNull();

        var suspension = result.Value.Items.Single(i => i.NewStatus == nameof(ClientStatus.Suspended));
        suspension.OldStatus.Should().Be(nameof(ClientStatus.Active));
        suspension.Reason.Should().Be("Compliance review");
        suspension.ActorUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task Paginates_and_clamps_a_nonsensical_page()
    {
        var client = ClientBuilder.Suspended(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        // Page 0 must be clamped to 1 rather than producing a negative Skip().
        var result = await BuildHandler().Handle(
            new GetStatusHistoryQuery(client.Id, Page: 0, PageSize: 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(2);
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(3);
        result.Value.HasNextPage.Should().BeTrue();
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_for_an_unknown_client()
    {
        var result = await BuildHandler().Handle(
            new GetStatusHistoryQuery(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_outside_the_agency_perimeter()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var result = await BuildHandler(OtherAgencyId).Handle(
            new GetStatusHistoryQuery(client.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Cannot_read_the_history_of_a_client_of_another_tenant()
    {
        var foreign = ClientBuilder.Active(OtherTenantId, AgencyId, UserId);
        await SeedAsync(foreign);

        var result = await BuildHandler().Handle(
            new GetStatusHistoryQuery(foreign.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
