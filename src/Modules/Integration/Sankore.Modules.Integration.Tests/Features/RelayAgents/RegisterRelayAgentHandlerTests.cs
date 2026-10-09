namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.RelayAgents.RegisterRelayAgent;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-27 criterion 1, the generating half: a single-use, short-lived token is minted, returned
/// once, and only its hash is kept.
/// </summary>
public sealed class RegisterRelayAgentHandlerTests
{
    private static RegisterRelayAgentHandler HandlerFor(
        Sankore.Modules.Integration.Infrastructure.IntegrationDbContext db, Guid tenantId)
        => new(
            db,
            RelayAgentsTestHarness.User(tenantId),
            RelayAgentsTestHarness.Clock(),
            RelayAgentsTestHarness.Log<RegisterRelayAgentHandler>());

    [Fact]
    public async Task It_returns_the_clear_token_and_stores_only_its_hash()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var db = factory.CreateContext();

        var result = await HandlerFor(db, RelayAgentsTestHarness.Tenant)
            .Handle(new RegisterRelayAgentCommand("Relais Abidjan"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.EnrolmentToken.Should().NotBeNullOrWhiteSpace();

        await using var read = factory.CreateContext();
        var stored = await read.RelayAgents.SingleAsync();

        // The hash is computed independently here, rather than through the production helper, so
        // the assertion pins WHAT is stored and not merely that two calls of the same method
        // agree: SHA-256 of the token, lower-case hexadecimal.
        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(result.Value.EnrolmentToken)));

        stored.EnrolmentTokenHash.Should().Be(expected);

        // And the clear value is nowhere in the row — not in the hash column, not in the name.
        stored.EnrolmentTokenHash.Should().NotBe(result.Value.EnrolmentToken);
        stored.Name.Should().NotContain(result.Value.EnrolmentToken);
    }

    [Fact]
    public async Task The_token_is_short_lived_and_the_agent_starts_pending()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var db = factory.CreateContext();

        var result = await HandlerFor(db, RelayAgentsTestHarness.Tenant)
            .Handle(new RegisterRelayAgentCommand("Relais Bouaké"), default);

        // Short-lived is half of "single-use and short-lived": the expiry is what bounds the
        // window in which a leaked token is worth anything.
        result.Value.ExpiresAt.Should().Be(RelayAgentsTestHarness.Now.AddMinutes(30));

        await using var read = factory.CreateContext();
        var stored = await read.RelayAgents.SingleAsync();

        stored.Status.Should().Be(RelayAgentStatus.Pending);
        stored.EnrolmentTokenExpiresAt.Should().Be(result.Value.ExpiresAt);
        stored.CertificateThumbprint.Should().BeNull("nothing is admitted until the exchange");
        stored.TenantId.Should().Be(RelayAgentsTestHarness.Tenant);
        stored.CreatedBy.Should().Be(RelayAgentsTestHarness.Actor);
    }

    [Fact]
    public async Task Two_registrations_mint_two_different_tokens()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var db = factory.CreateContext();

        var handler = HandlerFor(db, RelayAgentsTestHarness.Tenant);

        var first = await handler.Handle(new RegisterRelayAgentCommand("Site 1"), default);
        var second = await handler.Handle(new RegisterRelayAgentCommand("Site 2"), default);

        // Not a tautology worth skipping: a token derived from the agent name, the tenant or the
        // clock would pass every other test in this file and be guessable from an operator's
        // screen.
        second.Value.EnrolmentToken.Should().NotBe(first.Value.EnrolmentToken);
        second.Value.AgentId.Should().NotBe(first.Value.AgentId);
    }

    [Fact]
    public async Task The_tenant_comes_from_the_caller_and_not_from_the_request()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var db = factory.CreateContext();

        // The command has no tenant field at all — this pins that the row lands under the JWT's
        // tenant, which is the server-set rule of docs/integration-module-plan.md §5bis (a).
        await HandlerFor(db, RelayAgentsTestHarness.OtherTenant)
            .Handle(new RegisterRelayAgentCommand("Relais Dakar"), default);

        await using var read = factory.ContextFor(RelayAgentsTestHarness.OtherTenant);
        (await read.RelayAgents.SingleAsync()).TenantId
            .Should().Be(RelayAgentsTestHarness.OtherTenant);

        await using var otherSide = factory.CreateContext();
        (await otherSide.RelayAgents.CountAsync()).Should().Be(0);
    }
}
