namespace Sankore.Modules.Integration.Tests.Features.Connections;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections.CreateConnection;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

public sealed class CreateConnectionHandlerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    private CreateConnectionHandler Build(Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db)
        => new(db, ConnectionsTestHarness.User(Tenant), ConnectionsTestHarness.Clock(),
            ConnectionsTestHarness.Log<CreateConnectionHandler>());

    [Fact]
    public async Task A_connection_is_created_for_the_callers_tenant()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new CreateConnectionCommand(
                IntegrationFamily.CoreBanking, IntegrationKind.Temenos, IntegrationMode.Api,
                "CBS principal", ConnectionsTestHarness.Temenos()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == result.Value);

        stored.TenantId.Should().Be(Tenant);
        stored.Name.Should().Be("CBS principal");
        stored.Settings.Should().BeOfType<TemenosSettings>();
    }

    [Fact]
    public async Task A_new_connection_is_never_active_and_has_no_health_history()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new CreateConnectionCommand(
                IntegrationFamily.Insurance, IntegrationKind.Orass, IntegrationMode.Api,
                "ORASS vie", ConnectionsTestHarness.Orass()),
            CancellationToken.None);

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == result.Value);

        // The whole point of the three-step lifecycle: a connection cannot become the
        // destination of the tenant's writes without a health check in between.
        stored.IsActive.Should().BeFalse();
        stored.LastHealthStatus.Should().BeNull("null is \"never ran\", which is not a failure");
        stored.HasPassedHealthCheck.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_belonging_to_another_kind_are_refused_with_a_code_not_an_exception()
    {
        await using var db = _factory.CreateContext();

        // The validator refuses this first in production; the handler repeats the guard so the
        // aggregate's DomainException — a 500 — is never what the caller meets.
        var act = async () => await Build(db).Handle(
            new CreateConnectionCommand(
                IntegrationFamily.CoreBanking, IntegrationKind.Temenos, IntegrationMode.Api,
                "Mismatched", ConnectionsTestHarness.Amplitude()),
            CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Error.Should().Be(IntegrationErrors.SettingsInvalid);

        await using var read = _factory.CreateContext();
        (await read.Connections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Null_settings_are_refused()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new CreateConnectionCommand(
                IntegrationFamily.CoreBanking, IntegrationKind.Temenos, IntegrationMode.Api,
                "No settings", Settings: null),
            CancellationToken.None);

        result.Error.Should().Be(IntegrationErrors.SettingsInvalid);
    }

    [Fact]
    public async Task No_relay_agent_can_be_set_through_the_command()
    {
        await using var db = _factory.CreateContext();

        var result = await Build(db).Handle(
            new CreateConnectionCommand(
                IntegrationFamily.CoreBanking, IntegrationKind.PerfectVision, IntegrationMode.Relay,
                "Agence relais", new PerfectVisionSettings()),
            CancellationToken.None);

        await using var read = _factory.CreateContext();
        var stored = await read.Connections.SingleAsync(c => c.Id == result.Value);

        // A Relay connection is created WITHOUT its agent: the link is server-set by INT-27's
        // enrolment flow, the only place that can prove the agent belongs to this tenant.
        stored.RelayAgentId.Should().BeNull();
    }
}
