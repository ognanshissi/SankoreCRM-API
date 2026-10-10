namespace Sankore.Modules.Integration.Tests.Infrastructure.Transport;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Features.Batch.Outbound;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Criterion 4: "le dépôt se fait par SFTP, directement ou via l'agent relais" — and no caller
/// ever chooses which.
/// </summary>
public sealed class IntegrationFileTransportRoutingTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Other = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task A_batch_connection_with_no_agent_goes_out_directly()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await using var db = factory.CreateContext();

        var connection = OutboundBatchTestContext.BatchConnection(
            Tenant, OutboundBatchTestContext.AfterCutOff, id: ConnectionId);

        var result = await Router(db).PutAsync(connection, "f.csv", [1], CancellationToken.None);

        // Routed to the direct transport, which refuses the private test host — a relay route
        // would have answered RELAY_UNAVAILABLE instead.
        result.IsFailure.Should().BeTrue();
        result.Code.Should().NotBe(IntegrationErrors.RelayUnavailable);
    }

    [Fact]
    public async Task A_connection_carrying_an_agent_goes_through_the_relay()
    {
        var agentId = Guid.NewGuid();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.RelayAgents.Add(Agent(Tenant, agentId, active: true));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var connection = OutboundBatchTestContext.BatchConnection(
            Tenant, OutboundBatchTestContext.AfterCutOff, id: ConnectionId, relayAgentId: agentId);

        var result = await Router(db).PutAsync(connection, "f.csv", [1], CancellationToken.None);

        // INT-26/INT-27's command channel is not deployed, so the honest answer is Transient
        // RELAY_UNAVAILABLE: the file stays Generated and is retried the moment it exists.
        result.Code.Should().Be(IntegrationErrors.RelayUnavailable);
        result.Family.Should().Be(ErrorFamily.Transient);
    }

    [Fact]
    public async Task The_relay_never_falls_back_to_a_direct_connection()
    {
        // The one thing it must not do. A connection is routed through a relay precisely because
        // this process has no route into that network; "try it directly anyway" would at best fail
        // slowly and at worst reach a different host answering on the same name from our side.
        var agentId = Guid.NewGuid();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.RelayAgents.Add(Agent(Tenant, agentId, active: true));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var secrets = Substitute.For<ISecretsModule>();

        var connection = OutboundBatchTestContext.BatchConnection(
            Tenant, OutboundBatchTestContext.AfterCutOff, id: ConnectionId, relayAgentId: agentId);

        await Router(db, secrets).PutAsync(connection, "f.csv", [1], CancellationToken.None);

        // No credential was read, so nothing was dialled from this process.
        await secrets.DidNotReceiveWithAnyArgs().GetValueAsync(default!, default);
    }

    [Fact]
    public async Task An_agent_belonging_to_another_tenant_is_refused_without_revealing_it_exists()
    {
        // INT-27's second obligation (plan §5 ter): the dispatcher verifies at execution time that
        // agent.TenantId == connection.TenantId. Not a redundancy — the first guarantee lives in
        // the enrolment flow, this one lives in the path that actually moves the data, and the data
        // is a whole institution's day of customer writes.
        var foreignAgent = Guid.NewGuid();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.RelayAgents.Add(Agent(Other, foreignAgent, active: true));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var connection = OutboundBatchTestContext.BatchConnection(
            Tenant, OutboundBatchTestContext.AfterCutOff, id: ConnectionId,
            relayAgentId: foreignAgent);

        var result = await Router(db).PutAsync(connection, "f.csv", [1], CancellationToken.None);

        result.Code.Should().Be(IntegrationErrors.RelayUnavailable);

        // The answer must not distinguish "another tenant's agent" from "no agent": the caller
        // learns nothing about whether the id exists elsewhere.
        result.Detail.Should().NotContain(foreignAgent.ToString());
        result.Detail.Should().NotContain("tenant");
    }

    [Fact]
    public async Task A_revoked_agent_is_refused()
    {
        var revoked = Guid.NewGuid();

        using var factory = new TestIntegrationDbContextFactory(Tenant);

        await using (var seed = factory.CreateContext())
        {
            seed.RelayAgents.Add(Agent(Tenant, revoked, active: false));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var connection = OutboundBatchTestContext.BatchConnection(
            Tenant, OutboundBatchTestContext.AfterCutOff, id: ConnectionId, relayAgentId: revoked);

        var result = await Router(db).PutAsync(connection, "f.csv", [1], CancellationToken.None);

        result.Code.Should().Be(IntegrationErrors.RelayUnavailable);
        result.Family.Should().Be(ErrorFamily.Transient);
    }

    [Fact]
    public async Task An_agent_reference_that_resolves_to_nothing_degrades_rather_than_assuming()
    {
        using var factory = new TestIntegrationDbContextFactory(Tenant);
        await using var db = factory.CreateContext();

        var connection = OutboundBatchTestContext.BatchConnection(
            Tenant, OutboundBatchTestContext.AfterCutOff, id: ConnectionId,
            relayAgentId: Guid.NewGuid());

        var result = await Router(db).PutAsync(connection, "f.csv", [1], CancellationToken.None);

        // An opaque reference with no foreign key: a revoked agent leaves a dangling id and the
        // reader degrades to "relay unavailable" rather than assuming it resolves.
        result.Code.Should().Be(IntegrationErrors.RelayUnavailable);
    }

    private static IntegrationFileTransportRouter Router(
        Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db,
        ISecretsModule? secrets = null)
        => new(
            new SftpFileTransport(
                secrets ?? Substitute.For<ISecretsModule>(),
                new SftpEgressGuard(
                    Options.Create(new IntegrationEgressOptions()),
                    NullLogger<SftpEgressGuard>.Instance),
                NullLogger<SftpFileTransport>.Instance),
            new RelayFileTransport(db, NullLogger<RelayFileTransport>.Instance));

    private static IntegrationRelayAgent Agent(Guid tenantId, Guid id, bool active)
    {
        var now = new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);

        var agent = IntegrationRelayAgent.Enrol(
            tenantId: tenantId,
            name: $"Agent {id:N}"[..12],
            enrolmentTokenHash: new string('a', 64),
            tokenExpiresAt: now.AddHours(1),
            createdBy: Guid.NewGuid(),
            clock: clock,
            id: id);

        if (active)
        {
            var admitted = agent.Admit(new string('b', 64), now, clock);

            // A fixture that swallowed a failure would hand the tests a Pending agent, and every
            // "relay unavailable" assertion would pass by accident.
            if (admitted.IsFailure)
                throw new InvalidOperationException($"Test fixture could not admit: {admitted.Error}");
        }

        return agent;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
