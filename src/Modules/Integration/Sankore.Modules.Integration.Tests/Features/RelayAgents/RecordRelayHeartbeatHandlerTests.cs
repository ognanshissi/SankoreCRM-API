namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Features.RelayAgents;
using Sankore.Modules.Integration.Features.RelayAgents.RecordRelayHeartbeat;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-27 criterion 3, the ingestion half: a heartbeat updates last contact, version and latency,
/// and is authenticated by the certificate rather than by anything in its body.
/// </summary>
public sealed class RecordRelayHeartbeatHandlerTests
{
    private const string Thumbprint =
        "1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly DateTimeOffset Beat = RelayAgentsTestHarness.Now.AddMinutes(12);

    private static RecordRelayHeartbeatHandler HandlerFor(IntegrationDbContext db)
        => new(
            db,
            new RelayAgentAdmission(db, RelayAgentsTestHarness.Log<RelayAgentAdmission>()),
            RelayAgentsTestHarness.Clock(Beat),
            RelayAgentsTestHarness.Log<RecordRelayHeartbeatHandler>());

    [Fact]
    public async Task A_heartbeat_updates_last_contact_version_and_latency()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedActive(seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new RecordRelayHeartbeatCommand(Thumbprint, "1.4.2", 180, "SFTP reachable"), default);

        result.IsSuccess.Should().BeTrue();

        await using var read = factory.CreateContext();
        var stored = await read.RelayAgents.SingleAsync();

        stored.LastHeartbeatAt.Should().Be(Beat);
        stored.ReportedVersion.Should().Be("1.4.2");
        stored.ReportedLatencyMs.Should().Be(180);
        stored.ReportedStatusDetail.Should().Be("SFTP reachable");
    }

    [Fact]
    public async Task An_unknown_certificate_is_refused()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedActive(seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new RecordRelayHeartbeatCommand(new string('2', 64), "1.4.2", 10, null), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(IntegrationErrors.RelayAgentNotFound);

        await using var read = factory.CreateContext();

        // And nothing was written for the real agent: the whole danger of a forgeable heartbeat is
        // that it makes a dead relay look alive.
        (await read.RelayAgents.SingleAsync()).LastHeartbeatAt.Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_agents_heartbeat_is_refused()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var agent = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        var tracked = await seed.RelayAgents.AsTracking().SingleAsync(a => a.Id == agent.Id);
        tracked.Revoke(RelayAgentsTestHarness.Actor, RelayAgentsTestHarness.Clock());
        await seed.SaveChangesAsync();

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new RecordRelayHeartbeatCommand(Thumbprint, "1.4.2", 10, null), default);

        // Same code as an unknown certificate, deliberately: revoking erases the thumbprint, so
        // the two are the same state in the table and a distinguishable answer would confirm to
        // an unauthenticated caller that a certificate once existed.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(IntegrationErrors.RelayAgentNotFound);
    }

    [Fact]
    public async Task A_heartbeat_reaches_the_agent_of_the_tenant_the_certificate_belongs_to()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);

        await using var ours = factory.CreateContext();
        RelayAgentsTestHarness.SeedActive(
            ours, RelayAgentsTestHarness.Tenant, Thumbprint, name: "Ours");

        await using var theirs = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        var foreignThumbprint = new string('3', 64);
        var foreign = RelayAgentsTestHarness.SeedActive(
            theirs, RelayAgentsTestHarness.OtherTenant, foreignThumbprint, name: "Theirs");

        // The handler runs on a context bound to our tenant, as a public endpoint's scope would
        // be: the agent is resolved from the certificate, so the foreign agent's own heartbeat
        // must land on the foreign row and nowhere else.
        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new RecordRelayHeartbeatCommand(foreignThumbprint, "9.9.9", 5, null), default);

        result.IsSuccess.Should().BeTrue();

        await using var read = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        (await read.RelayAgents.SingleAsync(a => a.Id == foreign.Id)).ReportedVersion
            .Should().Be("9.9.9");

        await using var oursRead = factory.CreateContext();
        (await oursRead.RelayAgents.SingleAsync()).ReportedVersion.Should().BeNull();
    }

    [Fact]
    public async Task A_heartbeat_with_nothing_to_report_still_records_the_contact()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedActive(seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new RecordRelayHeartbeatCommand(Thumbprint, null, null, null), default);

        result.IsSuccess.Should().BeTrue();

        await using var read = factory.CreateContext();

        // "I am alive" is the minimum a heartbeat says, and it is the field an operator watches.
        (await read.RelayAgents.SingleAsync()).LastHeartbeatAt.Should().Be(Beat);
    }
}
