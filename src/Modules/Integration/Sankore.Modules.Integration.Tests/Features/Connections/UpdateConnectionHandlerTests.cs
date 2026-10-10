namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections.UpdateConnection;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class UpdateConnectionHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private UpdateConnectionHandler Build(Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db)
        => new(db, ConnectionsTestHarness.User(Tenant), ConnectionsTestHarness.Clock());

    [Fact]
    public async Task The_name_the_mode_and_the_settings_are_rewritten()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new UpdateConnectionCommand(
                ConnectionId: connection.Id,
                Name: "CBS secondaire",
                Mode: IntegrationMode.Api,
                Settings: ConnectionsTestHarness.Temenos("https://cbs2.example.ci/api/"),
                ExpectedVersion: connection.Version),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == connection.Id);

        stored.Name.Should().Be("CBS secondaire");
        ((TemenosSettings)stored.Settings!).BaseUrl.Should().Be("https://cbs2.example.ci/api/");
    }

    [Fact]
    public async Task An_unknown_connection_answers_not_found()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new UpdateConnectionCommand(
                Guid.NewGuid(), "X", IntegrationMode.Api,
                ConnectionsTestHarness.Temenos(), ExpectedVersion: 0),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task A_stale_row_version_is_a_concurrency_conflict()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new UpdateConnectionCommand(
                connection.Id, "X", IntegrationMode.Api,
                ConnectionsTestHarness.Temenos(),
                ExpectedVersion: connection.Version + 7),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConcurrencyConflict);
    }

    [Fact]
    public async Task A_stale_updated_at_is_a_concurrency_conflict_too()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant);

        await using var db = _factory.CreateContext();
        var handler = Build(db);

        // The alternative token, for a client that holds the DTO's updatedAt but not the row
        // version. The exact value passes; anything else is a conflict.
        var stale = await handler.Handle(
            new UpdateConnectionCommand(
                connection.Id, "X", IntegrationMode.Api, ConnectionsTestHarness.Temenos(),
                ExpectedUpdatedAt: connection.UpdatedAt.AddSeconds(-1)),
            CancellationToken.None);

        stale.Error.Should().Be(IntegrationErrors.ConcurrencyConflict);

        var fresh = await handler.Handle(
            new UpdateConnectionCommand(
                connection.Id, "X", IntegrationMode.Api, ConnectionsTestHarness.Temenos(),
                ExpectedUpdatedAt: connection.UpdatedAt),
            CancellationToken.None);

        fresh.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Settings_of_another_kind_than_the_stored_rows_are_refused()
    {
        await using var seed = _factory.CreateContext();
        var connection = ConnectionsTestHarness.Seed(seed, Tenant, kind: IntegrationKind.Temenos);

        await using var db = _factory.CreateContext();

        // The validator cannot catch this: the row's kind is in the database. Without the
        // handler's guard the aggregate would throw a DomainException — a 500 — instead.
        var result = await Build(db).Handle(
            new UpdateConnectionCommand(
                connection.Id, "X", IntegrationMode.Api,
                ConnectionsTestHarness.Amplitude(), ExpectedVersion: connection.Version),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.SettingsInvalid);
    }

    [Fact]
    public async Task An_existing_relay_agent_survives_an_ordinary_settings_edit()
    {
        await using var seed = _factory.CreateContext();

        // What INT-27's enrolment flow will have written. Reproduced through the aggregate's own
        // factory, which is the only writer of the field.
        var connection = IntegrationConnection.Create(
            Tenant, IntegrationFamily.CoreBanking, IntegrationKind.PerfectVision,
            IntegrationMode.Relay, "Relais agence", new PerfectVisionSettings(),
            ConnectionsTestHarness.Actor, ConnectionsTestHarness.Clock(),
            relayAgentId: Guid.Parse("55555555-5555-5555-5555-555555555555"));

        seed.Connections.Add(connection);
        await seed.SaveChangesAsync();

        await using var db = _factory.CreateContext();
        var result = await Build(db).Handle(
            new UpdateConnectionCommand(
                connection.Id, "Relais agence 2", IntegrationMode.Relay,
                new PerfectVisionSettings { BalanceViewName = "V_SOLDES" },
                ExpectedVersion: connection.Version),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == connection.Id);

        // Carried through, not cleared: the field is absent from the command, and an edit of the
        // coordinates must not silently detach the agent that executes them.
        stored.RelayAgentId.Should().Be(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    }
}
