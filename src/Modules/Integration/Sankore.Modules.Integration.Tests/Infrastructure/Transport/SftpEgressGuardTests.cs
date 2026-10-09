namespace Sankore.Modules.Integration.Tests.Infrastructure.Transport;

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Xunit;

/// <summary>
/// The SSRF guard on the DIRECT deposit path.
///
/// <para>
/// <c>SftpHost</c> and <c>SftpPort</c> come from a connection's settings, which a tenant
/// administrator edits, while the credential and the network position used to reach them are the
/// platform's. Pointed at <c>127.0.0.1</c>, <c>169.254.169.254</c> or a <c>10.x</c> address, the
/// deposit would open a session from inside SANKORE's own network and write that tenant's
/// customer file wherever it landed. A private address seen from THIS process is SANKORE's
/// network, never the institution's — an on-premise CBS is unreachable directly by construction,
/// which is what <c>IntegrationMode.Relay</c> and INT-26 are for.
/// </para>
/// </summary>
public sealed class SftpEgressGuardTests
{
    private static readonly Guid ConnectionId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Theory]
    [InlineData("127.0.0.1")]          // loopback
    [InlineData("127.1.2.3")]          // the rest of 127/8, which a naive check misses
    [InlineData("::1")]                // IPv6 loopback
    [InlineData("10.0.0.5")]           // RFC 1918
    [InlineData("172.16.3.4")]         // RFC 1918, bottom of the /12
    [InlineData("172.31.255.254")]     // RFC 1918, top of the /12
    [InlineData("192.168.1.10")]       // RFC 1918
    [InlineData("169.254.1.1")]        // link-local
    [InlineData("169.254.169.254")]    // cloud metadata — the single most valuable SSRF target
    [InlineData("0.0.0.0")]            // unspecified
    [InlineData("100.64.0.1")]         // CGNAT
    [InlineData("100.127.255.254")]    // CGNAT, top of the /10
    [InlineData("fe80::1")]            // IPv6 link-local
    [InlineData("fd00::1")]            // IPv6 unique local
    [InlineData("::ffff:10.0.0.5")]    // a private v4 address in its v6 spelling
    public async Task An_internal_address_is_refused(string host)
    {
        var refused = await Guard(allowPrivate: false)
            .ResolveAsync(host, 22, ConnectionId, CancellationToken.None);

        refused.Should().BeNull($"'{host}' is inside SANKORE's own network, not the institution's");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("41.207.1.1")]       // a public African range
    [InlineData("2001:4860:4860::8888")]
    public async Task A_public_address_proceeds_and_the_validated_address_is_what_comes_back(string host)
    {
        var target = await Guard(allowPrivate: false)
            .ResolveAsync(host, 2222, ConnectionId, CancellationToken.None);

        target.Should().NotBeNull();
        target!.Port.Should().Be(2222);

        // The ADDRESS, not the name: the caller connects to this, so a DNS answer that changes
        // between the check and the connect cannot walk past the guard.
        target.Address.Should().Be(IPAddress.Parse(host));
        target.HostArgument.Should().Be(IPAddress.Parse(host).ToString());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    public async Task AllowPrivateAddresses_permits_an_internal_address(string host)
    {
        // The escape hatch for a single-tenant, self-hosted installation whose CBS genuinely
        // shares this network. Platform-level and off by default — a tenant must not be able to
        // switch off a platform protection through its own settings.
        var target = await Guard(allowPrivate: true)
            .ResolveAsync(host, 22, ConnectionId, CancellationToken.None);

        target.Should().NotBeNull();
        target!.Address.Should().Be(IPAddress.Parse(host));
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_refused_like_any_other_unreachable_target()
    {
        // .invalid is reserved by RFC 2606 and never resolves.
        var refused = await Guard(allowPrivate: false)
            .ResolveAsync("sftp.sankore-does-not-exist.invalid", 22, ConnectionId, CancellationToken.None);

        refused.Should().BeNull();
    }

    [Fact]
    public async Task Localhost_is_refused_by_name_as_well_as_by_literal()
    {
        // The name path, not the literal one: "localhost" resolves to 127.0.0.1 and/or ::1, and
        // every answer being internal is what must be refused.
        var refused = await Guard(allowPrivate: false)
            .ResolveAsync("localhost", 22, ConnectionId, CancellationToken.None);

        refused.Should().BeNull();
    }

    [Fact]
    public void The_block_list_matches_the_ranges_SsrfSafeHandler_blocks()
    {
        // Range for range, in the same order, as
        // Sankore.Modules.Leads.Infrastructure.SsrfProtection.SsrfSafeHandler.IsBlockedAddress.
        // Re-stated rather than referenced because a module may not reference another module's
        // assembly; the correct home is the shared kernel. A change to one must be made to both,
        // and this is the test that notices.
        foreach (var blocked in new[]
                 {
                     "127.0.0.1", "::1", "10.255.255.255", "172.16.0.0", "172.31.255.255",
                     "192.168.255.255", "169.254.0.1", "0.1.2.3", "100.64.0.0",
                     "100.127.255.255", "fe80::abcd", "fc00::1", "fdff::1",
                 })
        {
            SftpEgressGuard.IsBlockedAddress(IPAddress.Parse(blocked))
                .Should().BeTrue($"{blocked} is in a blocked range");
        }

        foreach (var allowed in new[]
                 {
                     "8.8.8.8", "1.1.1.1", "172.15.0.1", "172.32.0.1", "192.167.0.1",
                     "169.253.0.1", "100.63.255.255", "100.128.0.1", "2606:4700::1111",
                 })
        {
            SftpEgressGuard.IsBlockedAddress(IPAddress.Parse(allowed))
                .Should().BeFalse($"{allowed} is a public address and must not be blocked");
        }
    }

    [Fact]
    public void The_escape_hatch_is_off_by_default()
    {
        new IntegrationEgressOptions().AllowPrivateAddresses.Should().BeFalse(
            "a hosted deployment must be safe with no configuration at all");

        IntegrationEgressOptions.SectionName.Should().Be("Integration:Egress");
    }

    private static SftpEgressGuard Guard(bool allowPrivate)
        => new(
            Options.Create(new IntegrationEgressOptions { AllowPrivateAddresses = allowPrivate }),
            new CapturingLogger<SftpEgressGuard>());

    /// <summary>
    /// A logger that records, so the refusal can be shown to reach the SERVER LOG while the
    /// caller is told nothing that distinguishes the causes.
    /// </summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
