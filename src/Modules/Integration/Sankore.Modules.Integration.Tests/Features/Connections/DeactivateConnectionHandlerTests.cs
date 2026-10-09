namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Features.Connections.DeactivateConnection;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class DeactivateConnectionHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private DeactivateConnectionHandler Build(Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db)
        => new(db, ConnectionsTestHarness.User(Tenant), ConnectionsTestHarness.Clock(),
            ConnectionsTestHarness.Log<DeactivateConnectionHandler>());

    [Fact]
    public async Task An_active_connection_is_switched_off_and_kept()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant, healthy: true, active: true);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new DeactivateConnectionCommand(connection.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == connection.Id);

        stored.IsActive.Should().BeFalse();

        // Never deleted: commands, references and call logs point at it.
        (await read.Connections.CountAsync()).Should().Be(1);

        // The passed health check survives, so re-activating does not require a second one.
        stored.HasPassedHealthCheck.Should().BeTrue();
    }

    [Fact]
    public async Task Deactivating_an_inactive_connection_is_a_success()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new DeactivateConnectionCommand(connection.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the caller's intent is already satisfied");
    }

    [Fact]
    public async Task An_unknown_connection_answers_not_found()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new DeactivateConnectionCommand(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }
}
