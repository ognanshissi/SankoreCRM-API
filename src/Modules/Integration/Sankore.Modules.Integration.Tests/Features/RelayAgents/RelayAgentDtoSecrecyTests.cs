namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Integration.Features.RelayAgents;
using Xunit;

/// <summary>
/// What the read side of INT-27 is allowed to return, asserted on the DTO's SHAPE.
///
/// <para>
/// A reflection test and not a handler test, because the failure mode is a one-line edit nobody
/// would question: "add the thumbprint, just for support". The thumbprint is not a secret — it is
/// a hash of a public certificate — but it is the identifier the agent channel authenticates on,
/// so a screen that displays it, a browser that caches it and a support ticket that quotes it all
/// become places to pick up an agent's identity. An operator needs to know whether the relay is
/// up, not what it authenticates with.
/// </para>
///
/// <para>
/// The enrolment token's hash is excluded for a different reason: it is what the exchange compares
/// against, so returning it would hand out the verifier for a live credential.
/// </para>
/// </summary>
public sealed class RelayAgentDtoSecrecyTests
{
    private static readonly string[] ForbiddenFragments = ["thumbprint", "hash", "certhash"];

    private static PropertyInfo[] Properties() =>
        typeof(RelayAgentDto).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void The_read_dto_exposes_neither_a_thumbprint_nor_a_token_hash()
    {
        var offenders = Properties()
            .Where(p => ForbiddenFragments.Any(
                f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "a thumbprint is the identifier the agent channel authenticates on, and a token hash "
            + "is the verifier of a live credential; neither belongs in a response an operator's "
            + "browser caches");
    }

    [Fact]
    public void The_only_token_shaped_field_is_an_expiry_timestamp()
    {
        var tokenish = Properties()
            .Where(p => p.Name.Contains("token", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The expiry is deliberately there: it is what lets a screen say "registered 40 minutes
        // ago, never collected its certificate", which is the one diagnosis an operator cannot
        // otherwise make. It is a timestamp, so it cannot be mistaken for the value.
        tokenish.Select(p => p.Name).Should().Equal("EnrolmentTokenExpiresAt");
        tokenish.Single().PropertyType.Should().Be(typeof(DateTimeOffset?));
    }

    [Fact]
    public void The_dto_still_carries_what_criterion_3_asks_for()
    {
        // The other half of the rule: a DTO trimmed until it leaks nothing would also satisfy the
        // two tests above while failing the criterion. Last contact, version and state are what
        // "heartbeats are exposed through the API" means.
        var names = Properties().Select(p => p.Name).ToList();

        names.Should().Contain("LastHeartbeatAt");
        names.Should().Contain("ReportedVersion");
        names.Should().Contain("Status");
    }
}
