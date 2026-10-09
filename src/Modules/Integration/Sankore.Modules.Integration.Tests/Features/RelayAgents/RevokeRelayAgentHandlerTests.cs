namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.RelayAgents;
using Sankore.Modules.Integration.Features.RelayAgents.RevokeRelayAgent;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-27 criterion 2: revocation invalidates the certificate, and the authoritative admission
/// check refuses from that instant.
/// </summary>
public sealed class RevokeRelayAgentHandlerTests
{
    private const string Thumbprint =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static RevokeRelayAgentHandler HandlerFor(
        IntegrationDbContext db, Guid tenantId, Guid? actor = null)
        => new(
            db,
            RelayAgentsTestHarness.User(tenantId, actor),
            RelayAgentsTestHarness.Clock(),
            RelayAgentsTestHarness.Log<RevokeRelayAgentHandler>());

    [Fact]
    public async Task Revocation_clears_the_thumbprint_and_the_admission_check_then_refuses()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var agent = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        // Admitted before: otherwise the assertion after the revocation would pass for an agent
        // that was never admitted in the first place.
        await using var before = factory.CreateContext();
        (await Admission(before).AdmitAsync(Thumbprint, default))
            .Should().NotBeNull();

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db, RelayAgentsTestHarness.Tenant)
            .Handle(new RevokeRelayAgentCommand(agent.Id), default);

        result.IsSuccess.Should().BeTrue();

        await using var read = factory.CreateContext();
        var stored = await read.RelayAgents.SingleAsync();

        stored.Status.Should().Be(RelayAgentStatus.Revoked);
        stored.CertificateThumbprint.Should().BeNull("the certificate must stop being admitted");
        stored.RevokedBy.Should().Be(RelayAgentsTestHarness.Actor);
        stored.RevokedAt.Should().Be(RelayAgentsTestHarness.Now);

        // This is the half of criterion 2 that lives in code: the check the channel runs on every
        // message now refuses. The other half is the channel actually running it per message,
        // which is a wiring obligation and not something this module can enforce.
        await using var after = factory.CreateContext();
        (await Admission(after).AdmitAsync(Thumbprint, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task Revoking_also_burns_an_enrolment_token_that_was_never_used()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var (agent, _) = RelayAgentsTestHarness.SeedPending(seed, RelayAgentsTestHarness.Tenant);

        await using var db = factory.CreateContext();
        await HandlerFor(db, RelayAgentsTestHarness.Tenant)
            .Handle(new RevokeRelayAgentCommand(agent.Id), default);

        await using var read = factory.CreateContext();
        var stored = await read.RelayAgents.SingleAsync();

        // Otherwise revoking an agent that had not yet collected its certificate would leave a
        // live credential behind, and the agent could enrol itself after being cut off.
        stored.EnrolmentTokenHash.Should().BeNull();
        stored.EnrolmentTokenExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task An_agent_of_another_tenant_is_not_found()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        var foreign = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.OtherTenant, Thumbprint);

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db, RelayAgentsTestHarness.Tenant)
            .Handle(new RevokeRelayAgentCommand(foreign.Id), default);

        result.IsFailure.Should().BeTrue();

        // 404 and never 403: a 403 would confirm that the id names a real agent somewhere on the
        // platform, which is an inventory of which institutions run an on-premise relay.
        result.Error.Should().Be(IntegrationErrors.RelayAgentNotFound);

        await using var read = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        (await read.RelayAgents.SingleAsync()).Status.Should().Be(RelayAgentStatus.Active);
    }

    [Fact]
    public async Task An_unknown_agent_is_not_found()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var db = factory.CreateContext();

        var result = await HandlerFor(db, RelayAgentsTestHarness.Tenant)
            .Handle(new RevokeRelayAgentCommand(Guid.NewGuid()), default);

        result.Error.Should().Be(IntegrationErrors.RelayAgentNotFound);
    }

    [Fact]
    public async Task Revoking_twice_succeeds_and_keeps_the_first_actor()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var agent = RelayAgentsTestHarness.SeedActive(
            seed, RelayAgentsTestHarness.Tenant, Thumbprint);

        await using var first = factory.CreateContext();
        await HandlerFor(first, RelayAgentsTestHarness.Tenant)
            .Handle(new RevokeRelayAgentCommand(agent.Id), default);

        await using var second = factory.CreateContext();
        var again = await HandlerFor(
                second, RelayAgentsTestHarness.Tenant, RelayAgentsTestHarness.SecondActor)
            .Handle(new RevokeRelayAgentCommand(agent.Id), default);

        // The caller's intent is already satisfied, so this is a success — and re-revoking must
        // not overwrite the original actor, which is the only record of who cut the agent off.
        again.IsSuccess.Should().BeTrue();

        await using var read = factory.CreateContext();
        (await read.RelayAgents.SingleAsync()).RevokedBy
            .Should().Be(RelayAgentsTestHarness.Actor);
    }

    private static IRelayAgentAdmission Admission(IntegrationDbContext db)
        => new RelayAgentAdmission(db, RelayAgentsTestHarness.Log<RelayAgentAdmission>());
}
