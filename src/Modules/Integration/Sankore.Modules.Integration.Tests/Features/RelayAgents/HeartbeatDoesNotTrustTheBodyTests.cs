namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;
using Sankore.Modules.Integration.Features.RelayAgents.RecordRelayHeartbeat;
using Xunit;

/// <summary>
/// <b>No request contract of this area accepts a certificate thumbprint, an agent id or a tenant
/// id.</b> Identity comes from the TLS handshake on the two public routes and from the JWT on the
/// operator ones — never from a payload.
///
/// <para>
/// <b>Why this is a security test and not a style test.</b> A thumbprint is a hash of a <i>public</i>
/// certificate: anyone who has ever seen the certificate — in a log, on a screen, in a config file
/// — can reproduce it. A route that trusted one from a body would let any caller who can reach it
/// post a heartbeat for any agent, and the consequence is not a wrong number on a dashboard: a
/// forged heartbeat makes a <b>dead relay look alive</b>. Batch files silently stop being
/// deposited, nothing is relayed into the institution's network, and the operations view stays
/// green — strictly worse than a relay that is visibly down. Only a TLS handshake proves
/// possession of the private key.
/// </para>
///
/// <para>
/// <b>There is no carve-out, including for the enrolment exchange.</b> At enrolment the token is
/// the credential and a body-supplied thumbprint would not have been forgeable in the same way —
/// whoever holds a valid token can pin a certificate they genuinely control anyway. But the
/// handshake gives proof of possession for free, with no certificate authority and no chain
/// validation: we are <i>pinning</i> a certificate, not trusting one. So the enrolment route takes
/// the certificate from the connection too, and this test has one rule rather than a rule plus an
/// exception — which is also one fewer thing for a future reader to reason about.
/// </para>
///
/// <para>
/// Asserted over the types by reflection, in the style of
/// <c>ConnectionRelayAgentNotBindableTests</c> — the suite that guards the same class of mistake
/// on the connection side. A validation rule can be written and later weakened; an absent field
/// cannot be sent at all.
/// </para>
/// </summary>
public sealed class HeartbeatDoesNotTrustTheBodyTests
{
    /// <summary>Every type a request body of this area binds into.</summary>
    private static readonly Type[] BindableTypes =
    [
        typeof(RecordRelayHeartbeatRequest),
        // Nested inside the heartbeat request, so it is just as client-bound and must face
        // the same identity-fragment assertions. A new record reachable from a request body
        // is a new place a thumbprint could be smuggled in.
        typeof(RelayTargetHealthReport),
        typeof(ExchangeEnrolmentTokenRequest),
        typeof(Sankore.Modules.Integration.Features.RelayAgents.RegisterRelayAgent
            .RegisterRelayAgentRequest),
    ];

    /// <summary>
    /// Fragments that, on a type populated from a request body, mean "the caller is asserting an
    /// identity". <c>agent</c> and <c>tenant</c> are in the list for the same reason the
    /// thumbprint is: §5bis (a) forbids an agent id in a request body, and a tenant id in one
    /// would be the same leak by a shorter route.
    /// </summary>
    private static readonly string[] IdentityFragments = ["thumbprint", "certificate", "agent", "tenant"];

    public static TheoryData<Type> ClientBoundTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in BindableTypes) data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(ClientBoundTypes))]
    public void No_request_contract_exposes_an_identity_field(Type type)
    {
        var offenders = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => IdentityFragments.Any(
                f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offenders.Should().BeEmpty(
            "{0} is populated from a request body. A thumbprint is a hash of a public "
            + "certificate, so one taken from a payload authenticates nobody — and a heartbeat "
            + "anybody can post makes a dead relay look alive",
            type.Name);
    }

    [Theory]
    [MemberData(nameof(ClientBoundTypes))]
    public void No_request_constructor_takes_one_either(Type type)
    {
        // A record's positional parameters are the other way the field could come back — through a
        // hand-written binder or a future [AsParameters] shape.
        var parameters = type.GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(p => p.Name ?? string.Empty)
            .ToList();

        parameters.Should().NotContain(
            name => IdentityFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)),
            "{0}", type.Name);
    }

    [Fact]
    public void The_heartbeat_request_carries_only_what_the_agent_reports_about_itself()
    {
        // A whitelist rather than a blacklist for this one type, because it is the route the two
        // reviews flagged: anything added to it is an assertion the agent makes about itself, and
        // the next field that is not version-, latency- or status-shaped should have to come past
        // this assertion.
        //
        // `Targets` came past it, deliberately. INT-26 criterion 5 asks for "la latence vers
        // CHAQUE système relié", which a single LatencyMs cannot carry, and the agent was already
        // sending the array while the endpoint truncated it to one number. Each entry is the
        // agent's own declared target NAME and KIND out of its own configuration file, plus a
        // reachability and a latency — an assertion about itself, with no identity of the agent in
        // it. `RelayTargetHealthReport` is in ClientBoundTypes above so its fields face the
        // identity-fragment assertions too, which is what actually keeps this safe rather than
        // the whitelist.
        typeof(RecordRelayHeartbeatRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo("Version", "LatencyMs", "StatusDetail", "Targets");
    }

    [Fact]
    public void The_enrolment_request_carries_only_the_token()
    {
        // The token IS the credential at enrolment; the certificate comes from the handshake.
        typeof(ExchangeEnrolmentTokenRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo("Token");
    }

    /// <summary>
    /// The two commands DO carry a thumbprint — computed by their endpoint from
    /// <c>HttpContext.Connection.GetClientCertificateAsync()</c> and never bound from a body,
    /// which the assertions above are what guarantee. Pinned as a closed list so a third such
    /// command cannot appear without somebody reading this comment.
    /// </summary>
    [Fact]
    public void Exactly_two_commands_carry_a_handshake_derived_thumbprint()
    {
        var carriers = typeof(Sankore.Modules.Integration.IntegrationModule).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Namespace?.StartsWith(
                            "Sankore.Modules.Integration.Features.RelayAgents",
                            StringComparison.Ordinal) == true)
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p => p.Name.Contains("Thumbprint", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        carriers.Should().Equal("ExchangeEnrolmentTokenCommand", "RecordRelayHeartbeatCommand");
    }
}
