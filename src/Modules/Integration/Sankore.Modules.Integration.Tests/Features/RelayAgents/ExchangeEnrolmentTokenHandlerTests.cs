namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// INT-27 criterion 1, the agent's half: the exchange admits a pending agent, burns the token, and
/// refuses everything else with ONE indistinguishable answer.
/// </summary>
public sealed class ExchangeEnrolmentTokenHandlerTests
{
    private const string Thumbprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static ExchangeEnrolmentTokenHandler HandlerFor(
        IntegrationDbContext db, DateTimeOffset? at = null)
        => new(
            db,
            RelayAgentsTestHarness.Clock(at),
            RelayAgentsTestHarness.Log<ExchangeEnrolmentTokenHandler>());

    [Fact]
    public async Task It_admits_a_pending_agent_and_burns_the_token()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var (agent, token) = RelayAgentsTestHarness.SeedPending(seed, RelayAgentsTestHarness.Tenant);

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new ExchangeEnrolmentTokenCommand(token, Thumbprint.ToUpperInvariant()), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgentId.Should().Be(agent.Id);

        // The tenant is an OUTPUT of the exchange. Nothing in the request named it, and this is
        // the mechanism §5bis (a) asks for: the link is established by enrolment, never by an id
        // in a body.
        result.Value.TenantId.Should().Be(RelayAgentsTestHarness.Tenant);

        await using var read = factory.CreateContext();
        var stored = await read.RelayAgents.SingleAsync();

        stored.Status.Should().Be(RelayAgentStatus.Active);

        // Lower-cased on the way in, whatever case the agent sent: PostgreSQL string equality is
        // case-sensitive, so a stored upper-case thumbprint would match nothing the admission
        // check later computes.
        stored.CertificateThumbprint.Should().Be(Thumbprint);
        stored.CertificateIssuedAt.Should().Be(RelayAgentsTestHarness.Now);

        // Burned. This clearing is what makes the token single-use; the short expiry is a
        // mitigation, not the guarantee.
        stored.EnrolmentTokenHash.Should().BeNull();
        stored.EnrolmentTokenExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task A_second_exchange_of_the_same_token_is_refused()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        var (_, token) = RelayAgentsTestHarness.SeedPending(seed, RelayAgentsTestHarness.Tenant);

        await using var first = factory.CreateContext();
        (await HandlerFor(first).Handle(
                new ExchangeEnrolmentTokenCommand(token, Thumbprint), default))
            .IsSuccess.Should().BeTrue();

        await using var second = factory.CreateContext();
        var replay = await HandlerFor(second).Handle(
            new ExchangeEnrolmentTokenCommand(
                token, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            default);

        replay.IsFailure.Should().BeTrue();
        replay.Error.Should().Be(IntegrationErrors.RelayEnrolmentNotPending);

        // And the first certificate still stands: a refused replay must not repoint an admitted
        // agent at somebody else's certificate.
        await using var read = factory.CreateContext();
        (await read.RelayAgents.SingleAsync()).CertificateThumbprint.Should().Be(Thumbprint);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();

        var (_, token) = RelayAgentsTestHarness.SeedPending(
            seed,
            RelayAgentsTestHarness.Tenant,
            tokenExpiresAt: RelayAgentsTestHarness.Now.AddMinutes(-1));

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new ExchangeEnrolmentTokenCommand(token, Thumbprint), default);

        result.IsFailure.Should().BeTrue();

        // NOT RelayEnrolmentExpired, even though the aggregate says so internally: told apart,
        // "expired" would confirm to an anonymous caller that the token it holds was real.
        result.Error.Should().Be(IntegrationErrors.RelayEnrolmentNotPending);

        await using var read = factory.CreateContext();
        (await read.RelayAgents.SingleAsync()).Status.Should().Be(RelayAgentStatus.Pending);
    }

    [Fact]
    public async Task A_revoked_agents_token_is_refused()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();

        var (agent, token) = RelayAgentsTestHarness.SeedPending(
            seed, RelayAgentsTestHarness.Tenant);

        var tracked = await seed.RelayAgents.AsTracking().SingleAsync(a => a.Id == agent.Id);
        tracked.Revoke(RelayAgentsTestHarness.Actor, RelayAgentsTestHarness.Clock());
        await seed.SaveChangesAsync();

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new ExchangeEnrolmentTokenCommand(token, Thumbprint), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(IntegrationErrors.RelayEnrolmentNotPending);
    }

    [Fact]
    public async Task An_unknown_token_is_refused()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();
        RelayAgentsTestHarness.SeedPending(seed, RelayAgentsTestHarness.Tenant);

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new ExchangeEnrolmentTokenCommand("not-a-token-anyone-minted", Thumbprint), default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(IntegrationErrors.RelayEnrolmentNotPending);
    }

    /// <summary>
    /// The property the four tests above each assert separately, asserted as the property it is:
    /// <b>one answer</b>. Separately they could all pass while still being three different codes,
    /// which is the enumeration oracle the single code exists to close.
    /// </summary>
    [Fact]
    public async Task Every_refusal_answers_the_same_code()
    {
        var errors = new List<string?>();

        foreach (var refusal in RefusalCases())
            errors.Add((await refusal()).Error);

        errors.Should().HaveCount(4);
        errors.Should().AllBe(IntegrationErrors.RelayEnrolmentNotPending);
        errors.Distinct().Should().HaveCount(1);
    }

    private static IEnumerable<Func<Task<Sankore.Shared.Kernel.Result<RelayAgentAdmissionDto>>>>
        RefusalCases()
    {
        // Unknown.
        yield return async () =>
        {
            using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
            await using var db = factory.CreateContext();

            return await HandlerFor(db).Handle(
                new ExchangeEnrolmentTokenCommand("nobody-minted-this", Thumbprint), default);
        };

        // Expired.
        yield return async () =>
        {
            using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
            await using var db = factory.CreateContext();
            var (_, token) = RelayAgentsTestHarness.SeedPending(
                db, RelayAgentsTestHarness.Tenant,
                tokenExpiresAt: RelayAgentsTestHarness.Now.AddSeconds(-1));

            return await HandlerFor(db).Handle(
                new ExchangeEnrolmentTokenCommand(token, Thumbprint), default);
        };

        // Already used.
        yield return async () =>
        {
            using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
            await using var db = factory.CreateContext();
            var (_, token) = RelayAgentsTestHarness.SeedPending(db, RelayAgentsTestHarness.Tenant);

            await HandlerFor(db).Handle(
                new ExchangeEnrolmentTokenCommand(token, Thumbprint), default);

            return await HandlerFor(db).Handle(
                new ExchangeEnrolmentTokenCommand(token, Thumbprint), default);
        };

        // Revoked.
        yield return async () =>
        {
            using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
            await using var db = factory.CreateContext();
            var (agent, token) = RelayAgentsTestHarness.SeedPending(
                db, RelayAgentsTestHarness.Tenant);

            var tracked = await db.RelayAgents.AsTracking().SingleAsync(a => a.Id == agent.Id);
            tracked.Revoke(RelayAgentsTestHarness.Actor, RelayAgentsTestHarness.Clock());
            await db.SaveChangesAsync();

            return await HandlerFor(db).Handle(
                new ExchangeEnrolmentTokenCommand(token, Thumbprint), default);
        };
    }

    [Fact]
    public async Task A_token_of_another_tenants_agent_still_resolves()
    {
        using var factory = new TestIntegrationDbContextFactory(RelayAgentsTestHarness.Tenant);
        await using var seed = factory.CreateContext();

        // Seeded for a DIFFERENT tenant than the context the handler runs on, which stands in for
        // the production situation: the presenter has no tenant at all. The exchange must still
        // resolve it — if it did not, every agent on the platform would be refused and nothing in
        // the logs would say why.
        var (agent, token) = RelayAgentsTestHarness.SeedPending(
            seed, RelayAgentsTestHarness.OtherTenant);

        await using var db = factory.CreateContext();
        var result = await HandlerFor(db).Handle(
            new ExchangeEnrolmentTokenCommand(token, Thumbprint), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgentId.Should().Be(agent.Id);
        result.Value.TenantId.Should().Be(RelayAgentsTestHarness.OtherTenant);
    }
}
