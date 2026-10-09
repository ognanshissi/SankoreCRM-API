namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Features.Connections.ActivateConnection;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class ActivateConnectionHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private ActivateConnectionHandler Build(Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db)
        => new(db, ConnectionsTestHarness.User(Tenant), ConnectionsTestHarness.Clock(),
            ConnectionsTestHarness.Log<ActivateConnectionHandler>());

    [Fact]
    public async Task Activation_without_a_passed_health_check_is_refused()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new ActivateConnectionCommand(connection.Id), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotHealthy);

        await using var read = _factory.CreateContext();
        (await read.Connections.SingleAsync(c => c.Id == connection.Id)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_health_check_does_not_unlock_activation()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        // A check that RAN and failed is not the same thing as never having run — and neither
        // opens the gate.
        var tracked = await seed.Connections.AsTracking().SingleAsync(c => c.Id == connection.Id);
        tracked.RecordHealth(
            IntegrationHealth.Unhealthy("Connection refused", ConnectionsTestHarness.Now),
            ConnectionsTestHarness.Clock());
        await seed.SaveChangesAsync();

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new ActivateConnectionCommand(connection.Id), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotHealthy);
    }

    [Fact]
    public async Task A_healthy_connection_is_activated()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant, healthy: true);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new ActivateConnectionCommand(connection.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        (await read.Connections.SingleAsync(c => c.Id == connection.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_active_core_banking_connection_is_refused()
    {
        await using var seed = _factory.CreateContext();
        ConnectionsTestHarness.Seed(seed, Tenant, name: "CBS en service", healthy: true, active: true);
        var second = ConnectionsTestHarness.Seed(
            seed, Tenant, kind: IntegrationKind.Sab, name: "SAB", healthy: true);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new ActivateConnectionCommand(second.Id), CancellationToken.None);

        // A customer cannot be created in two core banking systems. The InMemory provider
        // enforces no filtered index, so what this pins is the handler's explicit pre-check —
        // the friendly half of the guarantee. The index is the other half, and only PostgreSQL
        // can prove it.
        result.Error.Should().Be(IntegrationErrors.CoreBankingConnectionAlreadyActive);

        await using var read = _factory.CreateContext();
        (await read.Connections.SingleAsync(c => c.Id == second.Id)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Several_insurance_connections_may_be_active_at_once()
    {
        await using var seed = _factory.CreateContext();
        ConnectionsTestHarness.Seed(
            seed, Tenant, IntegrationFamily.Insurance, IntegrationKind.Orass,
            name: "ORASS vie", healthy: true, active: true);
        var second = ConnectionsTestHarness.Seed(
            seed, Tenant, IntegrationFamily.Insurance, IntegrationKind.Orass,
            name: "ORASS iard", healthy: true);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new ActivateConnectionCommand(second.Id), CancellationToken.None);

        // The asymmetry of ASS-01: an IMF legitimately distributes for several insurers.
        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        (await read.Connections.CountAsync(c => c.IsActive)).Should().Be(2);
    }

    [Fact]
    public async Task Activating_an_already_active_connection_is_a_success_not_a_conflict()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant, healthy: true, active: true);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new ActivateConnectionCommand(connection.Id), CancellationToken.None);

        // A double-clicked button must not read as the one-active-CBS rule firing.
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_connection_answers_not_found()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new ActivateConnectionCommand(Guid.NewGuid()), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }
}
