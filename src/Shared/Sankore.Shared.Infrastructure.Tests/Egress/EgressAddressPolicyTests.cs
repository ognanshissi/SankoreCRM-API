namespace Sankore.Shared.Infrastructure.Tests.Egress;

using System.Net;
using FluentAssertions;
using Sankore.Shared.Infrastructure.Egress;
using Xunit;

/// <summary>
/// The one block list every outbound call shares — lead pullers, workflow webhooks, SFTP deposits,
/// mail servers. The first group is what the two former copies already blocked, so the merge
/// cannot have loosened anything; the second is what they missed.
/// </summary>
public sealed class EgressAddressPolicyTests
{
    [Theory]
    // What Leads' SsrfSafeHandler and Integration's SftpEgressGuard both blocked.
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("0.1.2.3")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    [InlineData("fe80::abcd")]
    [InlineData("fc00::1")]
    [InlineData("fdff::1")]
    [InlineData("::ffff:10.0.0.1")]
    // What they missed.
    [InlineData("::")]                     // unspecified: connects to the local host on Linux
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]              // multicast
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]        // broadcast
    [InlineData("240.0.0.1")]              // reserved
    [InlineData("192.0.0.170")]            // IETF protocol assignments
    [InlineData("198.18.0.1")]             // benchmarking
    [InlineData("ff02::1")]                // IPv6 multicast
    [InlineData("fec0::1")]                // site-local
    [InlineData("::10.0.0.1")]             // IPv4-compatible
    [InlineData("64:ff9b::a9fe:a9fe")]     // NAT64 of 169.254.169.254
    [InlineData("64:ff9b:1::1")]           // local-use NAT64
    [InlineData("2002:0a00:0001::1")]      // 6to4 of 10.0.0.1
    [InlineData("2002:7f00:0001::1")]      // 6to4 of 127.0.0.1
    [InlineData("2001:0:4136:e378::1")]    // Teredo
    [InlineData("100::1")]                 // discard-only
    [InlineData("2001:db8::1")]            // documentation
    public void An_internal_or_reserved_address_is_blocked(string address)
        => EgressAddressPolicy.IsBlocked(IPAddress.Parse(address))
            .Should().BeTrue($"{address} is in a blocked range");

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("192.167.0.1")]
    [InlineData("169.253.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700::1111")]
    [InlineData("2a00:1450:4007:80e::200e")]
    [InlineData("64:ff9b::808:808")]       // NAT64 of 8.8.8.8 — the embedded address decides
    [InlineData("2002:0808:0808::1")]      // 6to4 of 8.8.8.8
    [InlineData("::ffff:8.8.8.8")]
    public void A_public_address_is_allowed(string address)
        => EgressAddressPolicy.IsBlocked(IPAddress.Parse(address))
            .Should().BeFalse($"{address} is a public address and must not be blocked");
}
