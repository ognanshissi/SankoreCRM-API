namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections.ActivateConnection;
using Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;
using Sankore.Modules.Integration.Features.Connections.DeactivateConnection;
using Sankore.Modules.Integration.Features.Connections.GetConnection;
using Sankore.Modules.Integration.Features.Connections.ListConnections;
using Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;
using Sankore.Modules.Integration.Features.Connections.UpdateConnection;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Cross-tenant isolation of the <c>integration/connections</c> zone.
///
/// <para>
/// Tenant B's row is written THROUGH a tenant-A context on purpose: EF's global query filters
/// apply to reads, not to inserts, so this seeds a genuine foreign-tenant row into the same
/// physical store — exactly the situation a shared PostgreSQL database creates. Every assertion
/// then checks that tenant A cannot reach it, by any route.
/// </para>
///
/// <para>
/// All of them must answer <c>INTEGRATION_CONNECTION_NOT_FOUND</c> and never a 403. The
/// difference matters here more than in most zones: a 403 would confirm that the id names a real
/// connection on the platform, which says that another institution is hosted here and — through
/// the error of a health check or an activation — which core banking system it runs.
/// </para>
/// </summary>
public sealed class ConnectionsTenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly TestIntegrationDbContextFactory _factory = new(TenantA);

    public void Dispose() => _factory.Dispose();

    /// <summary>Seeds a tenant-B connection through a tenant-A context.</summary>
    private async Task<IntegrationConnection> SeedForeignAsync()
    {
        await using var seed = _factory.CreateContext();

        return ConnectionsTestHarness.Seed(
            seed, TenantB, name: "CBS de l'autre IMF", healthy: true);
    }

    [Fact]
    public async Task Another_tenants_connection_is_invisible_to_the_detail_query()
    {
        var foreign = await SeedForeignAsync();

        await using var db = _factory.CreateContext();
        var result = await new GetConnectionHandler(db)
            .Handle(new GetConnectionQuery(foreign.Id), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task Another_tenants_connection_never_appears_in_the_list()
    {
        await SeedForeignAsync();

        await using var seed = _factory.CreateContext();
        ConnectionsTestHarness.Seed(seed, TenantA, name: "Mon CBS");

        await using var db = _factory.CreateContext();
        var result = await new ListConnectionsHandler(db)
            .Handle(new ListConnectionsQuery(), CancellationToken.None);

        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Name.Should().Be("Mon CBS");
    }

    [Fact]
    public async Task Another_tenants_connection_cannot_be_updated()
    {
        var foreign = await SeedForeignAsync();

        await using var db = _factory.CreateContext();
        var result = await new UpdateConnectionHandler(
                db, ConnectionsTestHarness.User(TenantA), ConnectionsTestHarness.Clock())
            .Handle(
                new UpdateConnectionCommand(
                    foreign.Id, "Détourné", IntegrationMode.Api,
                    ConnectionsTestHarness.Temenos("https://attacker.example/"),
                    ExpectedVersion: foreign.Version),
                CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);

        // And the row is untouched, read back through ITS OWN tenant's context — so the failure
        // above is isolation and not a broken query.
        await using var owner = _factory.ContextFor(TenantB);
        var stored = await owner.Connections.SingleAsync(c => c.Id == foreign.Id);

        stored.Name.Should().Be("CBS de l'autre IMF");
        ((TemenosSettings)stored.Settings!).BaseUrl.Should().NotContain("attacker");
    }

    [Fact]
    public async Task Another_tenants_connection_cannot_be_activated_or_deactivated()
    {
        var foreign = await SeedForeignAsync();

        await using var db = _factory.CreateContext();

        var activate = await new ActivateConnectionHandler(
                db, ConnectionsTestHarness.User(TenantA), ConnectionsTestHarness.Clock(),
                ConnectionsTestHarness.Log<ActivateConnectionHandler>())
            .Handle(new ActivateConnectionCommand(foreign.Id), CancellationToken.None);

        activate.Error.Should().Be(IntegrationErrors.ConnectionNotFound);

        var deactivate = await new DeactivateConnectionHandler(
                db, ConnectionsTestHarness.User(TenantA), ConnectionsTestHarness.Clock(),
                ConnectionsTestHarness.Log<DeactivateConnectionHandler>())
            .Handle(new DeactivateConnectionCommand(foreign.Id), CancellationToken.None);

        deactivate.Error.Should().Be(IntegrationErrors.ConnectionNotFound);
    }

    [Fact]
    public async Task Another_tenants_connection_cannot_be_health_checked()
    {
        var foreign = await SeedForeignAsync();
        var adapter = new StubCbsAdapter(IntegrationKind.Temenos);

        await using var db = _factory.CreateContext();
        var result = await new CheckConnectionHealthHandler(
                db, ConnectionsTestHarness.Resolver(db, adapter), ConnectionsTestHarness.Clock(),
                ConnectionsTestHarness.Log<CheckConnectionHealthHandler>())
            .Handle(new CheckConnectionHealthCommand(foreign.Id), CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);

        // Not even a call to the far end: a health check fired at a foreign connection would be
        // both a port scan of another institution's network and a way to learn it exists.
        adapter.Calls.Should().Be(0);
    }

    [Fact]
    public async Task No_credential_can_be_written_onto_another_tenants_connection()
    {
        var foreign = await SeedForeignAsync();
        var secrets = Substitute.For<ISecretsModule>();

        await using var db = _factory.CreateContext();
        var result = await new SetConnectionSecretHandler(
                db, secrets, ConnectionsTestHarness.User(TenantA),
                ConnectionsTestHarness.Log<SetConnectionSecretHandler>())
            .Handle(
                new SetConnectionSecretCommand(foreign.Id, "connection-credential", "k"),
                CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.ConnectionNotFound);

        await secrets.DidNotReceive().SetAsync(
            Arg.Any<SecretKey>(), Arg.Any<string>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_active_core_banking_connection_of_another_tenant_does_not_block_this_one()
    {
        await using var seed = _factory.CreateContext();

        // Tenant B's CBS is active. The rule is ONE PER TENANT, so it must not stand in the way
        // of tenant A activating its own — a missing tenant predicate in the pre-check would make
        // the first institution to go live lock out every other one on the platform.
        ConnectionsTestHarness.Seed(seed, TenantB, name: "CBS B", healthy: true, active: true);
        var mine = ConnectionsTestHarness.Seed(seed, TenantA, name: "CBS A", healthy: true);

        await using var db = _factory.CreateContext();
        var result = await new ActivateConnectionHandler(
                db, ConnectionsTestHarness.User(TenantA), ConnectionsTestHarness.Clock(),
                ConnectionsTestHarness.Log<ActivateConnectionHandler>())
            .Handle(new ActivateConnectionCommand(mine.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
