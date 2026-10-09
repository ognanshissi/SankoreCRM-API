namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.RelayAgents;
using Sankore.Modules.Integration.Features.RelayAgents.ListRelayAgents;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-27 criterion 3, the read half: last contact, version and state are exposed through the API,
/// and nothing else is.
/// </summary>
public sealed class ListRelayAgentsHandlerTests
{
    private const string Thumbprint =
        "4444444444444444444444444444444444444444444444444444444444444444";

    private static ListRelayAgentsHandler HandlerFor(IntegrationDbContext db)
        => new(db, RelayAgentsTestHarness.Clock(RelayAgentsTestHarness.Now.AddMinutes(5)));

    [Fact]
    public async Task It_exposes_the_last_contact_the_version_and_the_state()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var agent = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.Tenant, Thumbprint, name: "Relais Abidjan");

        var tracked = await seed.RelayAgents.AsTracking().SingleAsync(a => a.Id == agent.Id);
        // Two targets, so the read side is exercised against what criterion 5 actually sends:
        // the worst latency in the sortable column, the whole array in the jsonb.
        tracked.RecordHeartbeat(
            "1.4.2", 180, "SFTP reachable",
            """
            [{"name":"cbs","kind":"Http","state":"Reachable","latencyMs":40},
             {"name":"depot","kind":"Sftp","state":"Reachable","latencyMs":180}]
            """,
            RelayAgentsTestHarness.Clock(RelayAgentsTestHarness.Now.AddMinutes(2)));
        await seed.SaveChangesAsync();

        await using var db = factory.CreateContext();
        var rows = (await HandlerFor(db).Handle(new ListRelayAgentsQuery(), default)).Value;

        rows.Should().HaveCount(1);
        var row = rows[0];

        row.Name.Should().Be("Relais Abidjan");
        row.Status.Should().Be(RelayAgentStatus.Active);
        row.LastHeartbeatAt.Should().Be(RelayAgentsTestHarness.Now.AddMinutes(2));
        row.ReportedVersion.Should().Be("1.4.2");
        row.ReportedLatencyMs.Should().Be(180);
        row.ReportedStatusDetail.Should().Be("SFTP reachable");
        row.CertificateIssuedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_pending_agent_whose_token_has_run_out_is_no_longer_reported_as_pending()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();

        RelayAgentsTestHarness.SeedPending(
            seed, RelayAgentsTestHarness.Tenant, name: "Fresh",
            tokenExpiresAt: RelayAgentsTestHarness.Now.AddMinutes(30));

        RelayAgentsTestHarness.SeedPending(
            seed, RelayAgentsTestHarness.Tenant, name: "Stale",
            tokenExpiresAt: RelayAgentsTestHarness.Now.AddMinutes(1));

        // The clock is Now + 5 minutes, so "Stale"'s window has closed.
        await using var db = factory.CreateContext();
        var rows = (await HandlerFor(db).Handle(new ListRelayAgentsQuery(), default)).Value;

        var fresh = rows.Single(r => r.Name == "Fresh");
        var stale = rows.Single(r => r.Name == "Stale");

        // Both are Pending as a status; only one is still waiting for something that can happen.
        // The distinction is the one diagnosis an operator cannot otherwise make — a stale agent
        // needs a new registration, not patience.
        fresh.IsEnrolmentPending.Should().BeTrue();
        stale.IsEnrolmentPending.Should().BeFalse();
        stale.Status.Should().Be(RelayAgentStatus.Pending);
    }

    [Fact]
    public async Task Live_agents_come_before_revoked_ones()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();

        var revoked = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.Tenant, Thumbprint, name: "AAA revoked");

        var tracked = await seed.RelayAgents.AsTracking().SingleAsync(a => a.Id == revoked.Id);
        tracked.Revoke(RelayAgentsTestHarness.Actor, RelayAgentsTestHarness.Clock());
        await seed.SaveChangesAsync();

        RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.Tenant, new string('5', 64), name: "ZZZ live");

        await using var db = factory.CreateContext();
        var rows = (await HandlerFor(db).Handle(new ListRelayAgentsQuery(), default)).Value;

        // Ordered by state before name, so the screen's first question — which relays are live —
        // is answered by the top of the list rather than by reading every row.
        rows.Select(r => r.Name).Should().ContainInOrder("ZZZ live", "AAA revoked");
    }

    [Fact]
    public async Task Another_tenants_agents_are_not_in_the_result()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);

        await using var theirs = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        RelayAgentsTestHarness.SeedActive(
            theirs, RelayAgentsTestHarness.OtherTenant, Thumbprint, name: "Theirs");

        await using var db = factory.CreateContext();
        var rows = (await HandlerFor(db).Handle(new ListRelayAgentsQuery(), default)).Value;

        // The global query filter, not a predicate in the handler. This is the one read of the
        // aggregate that HAS a tenant context — the exchange and the admission check deliberately
        // bypass the filter — so the contrast is worth pinning.
        rows.Should().BeEmpty();

        await using var their = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        (await HandlerFor(their).Handle(new ListRelayAgentsQuery(), default))
            .Value.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_read_side_exposes_no_certificate_thumbprint()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedActive(seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        await using var db = factory.CreateContext();
        var rows = (await HandlerFor(db).Handle(new ListRelayAgentsQuery(), default)).Value;

        // Asserted on the VALUES and not only on the DTO's shape (which
        // RelayAgentDtoSecrecyTests covers): a thumbprint smuggled into Name or
        // ReportedStatusDetail would pass a reflection test and still be returned.
        var serialised = System.Text.Json.JsonSerializer.Serialize(rows);

        serialised.Should().NotContain(Thumbprint);
        serialised.Should().NotContain(Thumbprint.ToUpperInvariant());
    }
}
