namespace Sankore.Modules.Integration.Infrastructure.Transport;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Resolves an SFTP target and refuses one that points inside SANKORE's own network (SSRF).
///
/// <para>
/// <b>Why this is needed on the DIRECT path.</b> <c>SftpHost</c> and <c>SftpPort</c> come from a
/// connection's settings, which a tenant administrator edits; the network position and the
/// credential used to reach them are the platform's. Pointed at <c>127.0.0.1</c>,
/// <c>169.254.169.254</c> or a <c>10.x</c> address, this process would open an SFTP session from
/// inside its own network and deposit a file carrying that tenant's customers wherever it landed
/// — or read an internal service back. Only the target is the tenant's; everything that makes the
/// request powerful is ours.
/// </para>
///
/// <para>
/// <b>It corrects a claim in docs/integration-module-plan.md §1</b>, which argued that SSRF
/// protection is inapplicable here "because a CBS is frequently on-premise at RFC 1918". That is
/// true of the CBS and false of this code path: SANKORE is hosted and multi-tenant, so an address
/// in private space as seen from THIS process is SANKORE's network, not the IMF's. A genuinely
/// on-premise system is unreachable directly by construction — which is why
/// <c>IntegrationMode.Relay</c> and INT-26 exist. <c>Api</c> and <c>Batch</c> go over the public
/// internet; <c>Relay</c> is the sanctioned route to a private target and opens no socket here.
/// </para>
///
/// <para>
/// <b>The block list is the one in</b> <c>Sankore.Modules.Leads.Infrastructure.SsrfProtection.SsrfSafeHandler</c>,
/// range for range. It is re-stated rather than referenced because a module may not reference
/// another module's assembly — the correct home for it is the shared kernel, and hoisting it
/// there touches a shared project outside this slice's ownership (reported, not done). Any change
/// to one must be made to the other; the ranges are listed in the same order so a diff of the two
/// methods is readable.
/// </para>
///
/// <para>
/// <b>Validated address in, connection out.</b> The caller connects to the
/// <see cref="EgressTarget.Address"/> this returns and never re-resolves the name — otherwise a
/// DNS answer that changes between the check and the connect (rebinding) walks straight past it.
/// </para>
/// </summary>
internal sealed class SftpEgressGuard(
    IOptions<IntegrationEgressOptions> options,
    ILogger<SftpEgressGuard> logger)
{
    /// <summary>
    /// The address to connect to, or <c>null</c> when the target is refused. The reason is logged
    /// here and never returned: three distinguishable failures would let somebody map our
    /// internal network one address at a time, which is the prize once connecting is blocked.
    /// </summary>
    internal async Task<EgressTarget?> ResolveAsync(
        string host, int port, Guid connectionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var allowPrivate = options.Value.AllowPrivateAddresses;

        // A literal address is not resolved: Dns.GetHostAddressesAsync happens to accept one, but
        // going through it would make the guard depend on a name lookup for an input that has no
        // name — and on a host with no DNS it would fail a target that is perfectly valid.
        if (IPAddress.TryParse(host, out var literal))
        {
            if (!allowPrivate && IsBlockedAddress(literal))
            {
                Refuse(connectionId, host, "the configured literal address is in private, "
                                           + "loopback, link-local or CGNAT space");
                return null;
            }

            return new EgressTarget(literal, port);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // A name that does not resolve is reported like every other unreachable target, so
            // "no such host" and "host is internal" cannot be told apart from outside.
            logger.LogWarning(
                ex, "SFTP host '{Host}' of connection {ConnectionId} did not resolve",
                host, connectionId);

            return null;
        }

        if (addresses.Length == 0)
        {
            Refuse(connectionId, host, "the name resolved to no address");
            return null;
        }

        if (allowPrivate) return new EgressTarget(addresses[0], port);

        foreach (var address in addresses)
        {
            if (!IsBlockedAddress(address)) return new EgressTarget(address, port);
        }

        // EVERY answer was internal. Refusing rather than picking the least-bad one: a name that
        // resolves only into our own network is either a mistake or an attempt, and neither is a
        // deposit target.
        Refuse(
            connectionId, host,
            $"all {addresses.Length} resolved address(es) are in private, loopback, link-local or "
            + "CGNAT space");

        return null;
    }

    /// <summary>
    /// Logged at Error with the address family named, because an operator investigating a refused
    /// deposit needs to see what the name pointed at — and because on a legitimately self-hosted
    /// installation this line is the one that tells them to set
    /// <c>Integration:Egress:AllowPrivateAddresses</c>.
    /// </summary>
    private void Refuse(Guid connectionId, string host, string reason)
        => logger.LogError(
            "Refusing a direct SFTP connection for connection {ConnectionId}: {Reason} (host "
            + "'{Host}'). A private address seen from this process is SANKORE's own network, not "
            + "the institution's; route an on-premise system through a relay agent "
            + "(IntegrationMode.Relay, INT-26). A single-tenant self-hosted deployment may set "
            + "{Setting}:AllowPrivateAddresses to true.",
            connectionId, reason, host, IntegrationEgressOptions.SectionName);

    /// <summary>
    /// Private, loopback, link-local (cloud metadata included), unspecified and CGNAT space.
    /// Kept range-for-range identical to <c>SsrfSafeHandler.IsBlockedAddress</c>.
    /// </summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Loopback (127.0.0.0/8, ::1)
        if (IPAddress.IsLoopback(address)) return true;

        // An IPv4 address written as ::ffff:10.0.0.1 is the same address; mapping it back is what
        // stops the v6 spelling of a private v4 target from skipping the v4 branch entirely.
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // Link-local 169.254.0.0/16 — the cloud metadata service at 169.254.169.254 lives
            // here, and it is the single most valuable target an SSRF can reach.
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 0.0.0.0/8
            if (bytes[0] == 0) return true;

            // 100.64.0.0/10 (carrier-grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = addr.GetAddressBytes();

            // Link-local fe80::/10
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true;

            // Unique local fc00::/7
            if ((bytes[0] & 0xfe) == 0xfc) return true;
        }

        return false;
    }
}

/// <summary>
/// A target that passed the guard. The <see cref="Address"/> is what the caller must connect to —
/// handing the NAME back would reopen the rebinding window the guard just closed.
/// </summary>
internal sealed record EgressTarget(IPAddress Address, int Port)
{
    /// <summary>
    /// The address as SSH.NET's <c>ConnectionInfo</c> wants it. An IPv6 literal is NOT bracketed:
    /// the host argument is fed to <c>Dns</c>/<c>IPAddress.Parse</c>, not to a URI parser, and
    /// brackets would make it unparseable.
    /// </summary>
    internal string HostArgument => Address.ToString();
}
