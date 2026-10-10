namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Sankore.Modules.Integration.Features.Connections.ListConnections;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class ListConnectionsHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Every_connection_of_the_tenant_is_listed_active_first()
    {
        await using var seed = _factory.CreateContext();
        ConnectionsTestHarness.Seed(seed, Tenant, name: "CBS", healthy: true, active: true);
        ConnectionsTestHarness.Seed(
            seed, Tenant, IntegrationFamily.Insurance, IntegrationKind.Orass, name: "ORASS");

        await using var db = _factory.CreateContext();
        var result = await new ListConnectionsHandler(db)
            .Handle(new ListConnectionsQuery(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(2);
        result.Value.Items[0].Name.Should().Be("CBS", "the screen's first question is what is live");
    }

    [Fact]
    public async Task The_family_filter_separates_the_two_families()
    {
        await using var seed = _factory.CreateContext();
        ConnectionsTestHarness.Seed(seed, Tenant, name: "CBS");
        ConnectionsTestHarness.Seed(
            seed, Tenant, IntegrationFamily.Insurance, IntegrationKind.Orass, name: "ORASS vie");
        ConnectionsTestHarness.Seed(
            seed, Tenant, IntegrationFamily.Insurance, IntegrationKind.Orass, name: "ORASS iard");

        await using var db = _factory.CreateContext();
        var handler = new ListConnectionsHandler(db);

        var insurance = await handler.Handle(
            new ListConnectionsQuery(Family: IntegrationFamily.Insurance), CancellationToken.None);

        insurance.Value.TotalCount.Should().Be(2);
        insurance.Value.Items.Should().OnlyContain(c => c.Family == IntegrationFamily.Insurance);
    }

    [Fact]
    public async Task The_activity_filter_answers_what_is_live()
    {
        await using var seed = _factory.CreateContext();
        ConnectionsTestHarness.Seed(seed, Tenant, name: "CBS", healthy: true, active: true);
        ConnectionsTestHarness.Seed(
            seed, Tenant, IntegrationFamily.Insurance, IntegrationKind.Orass, name: "ORASS");

        await using var db = _factory.CreateContext();
        var handler = new ListConnectionsHandler(db);

        (await handler.Handle(new ListConnectionsQuery(IsActive: true), CancellationToken.None))
            .Value.Items.Should().ContainSingle().Which.Name.Should().Be("CBS");

        (await handler.Handle(new ListConnectionsQuery(IsActive: false), CancellationToken.None))
            .Value.Items.Should().ContainSingle().Which.Name.Should().Be("ORASS");
    }

    [Fact]
    public async Task A_page_below_one_is_clamped_rather_than_producing_a_negative_offset()
    {
        await using var seed = _factory.CreateContext();
        ConnectionsTestHarness.Seed(seed, Tenant, name: "CBS");

        await using var db = _factory.CreateContext();
        var result = await new ListConnectionsHandler(db)
            .Handle(new ListConnectionsQuery(Page: 0, PageSize: 0), CancellationToken.None);

        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(1);
        result.Value.Items.Should().ContainSingle();
    }
}
