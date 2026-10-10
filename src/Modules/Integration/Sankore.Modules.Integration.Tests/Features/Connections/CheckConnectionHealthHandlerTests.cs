namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class CheckConnectionHealthHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private static CheckConnectionHealthHandler Build(
        IntegrationDbContext db, params Sankore.Modules.Integration.Ports.ICbsAdapter[] adapters)
        => new(db, ConnectionsTestHarness.Resolver(db, adapters), ConnectionsTestHarness.Clock(),
            ConnectionsTestHarness.Log<CheckConnectionHealthHandler>());

    [Fact]
    public async Task A_successful_check_is_recorded_with_its_date_and_latency()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        var adapter = new StubCbsAdapter(
            IntegrationKind.Temenos,
            IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(81), ConnectionsTestHarness.Now));

        await using var db = _factory.CreateContext();
        var result = await Build(db, adapter).Handle(
            new CheckConnectionHealthCommand(connection.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsHealthy.Should().BeTrue();
        result.Value.LatencyMs.Should().Be(81);
        adapter.Calls.Should().Be(1);

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == connection.Id);

        stored.LastHealthStatus.Should().BeTrue();
        stored.LastHealthAt.Should().Be(ConnectionsTestHarness.Now);
        stored.HasPassedHealthCheck.Should().BeTrue("this is what unlocks activation");
    }

    [Fact]
    public async Task An_unreachable_system_is_a_recorded_result_and_not_an_error()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        var adapter = new StubCbsAdapter(
            IntegrationKind.Temenos,
            IntegrationHealth.Unhealthy("503 from the gateway", ConnectionsTestHarness.Now));

        await using var db = _factory.CreateContext();
        var result = await Build(db, adapter).Handle(
            new CheckConnectionHealthCommand(connection.Id), CancellationToken.None);

        // The caller asked "did it answer?" and got an answer. A failure Result would make the
        // operator's own question look like a bug in the platform.
        result.IsSuccess.Should().BeTrue();
        result.Value.IsHealthy.Should().BeFalse();

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == connection.Id);

        stored.LastHealthStatus.Should().BeFalse();
        stored.LastHealthDetail.Should().Be("503 from the gateway");
    }

    [Fact]
    public async Task A_throwing_adapter_still_leaves_a_trace_and_never_leaks_its_message()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        var adapter = new StubCbsAdapter(
            IntegrationKind.Temenos,
            throws: new HttpRequestException(
                "GET https://cbs.example.ci/api/?token=super-secret failed"));

        await using var db = _factory.CreateContext();
        var result = await Build(db, adapter).Handle(
            new CheckConnectionHealthCommand(connection.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsHealthy.Should().BeFalse();

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == connection.Id);

        stored.LastHealthStatus.Should().BeFalse();

        // The TYPE name, never the message: an HTTP stack's message routinely carries the request
        // URI, and LastHealthDetail is returned by a GET.
        stored.LastHealthDetail.Should().Contain(nameof(HttpRequestException));
        stored.LastHealthDetail.Should().NotContain("super-secret");
    }

    [Fact]
    public async Task A_kind_with_no_registered_adapter_answers_a_clear_distinct_code()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant, kind: IntegrationKind.Sab);

        await using var db = _factory.CreateContext();

        // Only a Temenos adapter is registered; the connection is a SAB one.
        var result = await Build(db, new StubCbsAdapter(IntegrationKind.Temenos)).Handle(
            new CheckConnectionHealthCommand(connection.Id), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.AdapterNotRegistered);

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == connection.Id);

        // Nothing recorded: a missing assembly in OUR build is not an outage at the IMF's CBS,
        // and writing it into the health columns would say it was.
        stored.LastHealthAt.Should().BeNull();
        stored.LastHealthStatus.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_connection_answers_not_found()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db, new StubCbsAdapter(IntegrationKind.Temenos)).Handle(
            new CheckConnectionHealthCommand(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }
}
