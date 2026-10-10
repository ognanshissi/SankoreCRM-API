namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.RelayAgents;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// Doubles and builders shared by the relay-agent slice tests (INT-27). Everything is built with
/// <c>new</c>: no container, no host, no MediatR pipeline — a handler test that needed DI would be
/// testing the wiring instead of the decision.
/// </summary>
internal static class RelayAgentsTestHarness
{
    internal static readonly DateTimeOffset Now = new(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);

    internal static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    internal static readonly Guid OtherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static readonly Guid Actor = Guid.Parse("44444444-4444-4444-4444-444444444444");

    internal static readonly Guid SecondActor = Guid.Parse("55555555-5555-5555-5555-555555555555");

    internal static TimeProvider Clock(DateTimeOffset? at = null) => new FixedClock(at ?? Now);

    internal static ICurrentUser User(Guid tenantId, Guid? userId = null)
        => new TestCurrentUser(userId ?? Actor, tenantId);

    internal static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    /// <summary>
    /// A well-formed SHA-256 thumbprint built from one hex character, so a test can name two
    /// distinguishable certificates without a 64-character literal in the middle of an assertion.
    /// </summary>
    internal static string Thumbprint(char hexDigit) => new(hexDigit, 64);

    /// <summary>
    /// Seeds an agent with an armed enrolment token and hands back the CLEAR token, which is the
    /// thing a test needs and the thing production code can never recover.
    ///
    /// <para>
    /// Written through the context it is given, so a test can seed a FOREIGN tenant's row through
    /// its own context — EF's query filters apply to reads, not to inserts.
    /// </para>
    /// </summary>
    internal static (IntegrationRelayAgent Agent, string Token) SeedPending(
        IntegrationDbContext db,
        Guid tenantId,
        string name = "Relais Abidjan",
        DateTimeOffset? tokenExpiresAt = null,
        DateTimeOffset? at = null)
    {
        var clock = Clock(at);
        var token = RelayEnrolmentToken.Generate();

        var agent = IntegrationRelayAgent.Enrol(
            tenantId: tenantId,
            name: name,
            enrolmentTokenHash: RelayEnrolmentToken.Hash(token),
            tokenExpiresAt: tokenExpiresAt ?? (at ?? Now).AddMinutes(30),
            createdBy: Actor,
            clock: clock);

        db.RelayAgents.Add(agent);
        db.SaveChanges();

        return (agent, token);
    }

    /// <summary>An agent that has already exchanged its token, with a known thumbprint.</summary>
    internal static IntegrationRelayAgent SeedActive(
        IntegrationDbContext db,
        Guid tenantId,
        string thumbprint,
        string name = "Relais Abidjan",
        DateTimeOffset? at = null)
    {
        var clock = Clock(at);
        var (agent, _) = SeedPending(db, tenantId, name, at: at);

        agent.Admit(thumbprint, (at ?? Now).AddMinutes(-1), clock);
        db.SaveChanges();

        return agent;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record TestCurrentUser(Guid Id, Guid TenantId) : ICurrentUser
    {
        public string DisplayName => "test";

        public bool IsAuthenticated => true;

        public IReadOnlyList<string> Roles => ["Administrator"];
    }
}
