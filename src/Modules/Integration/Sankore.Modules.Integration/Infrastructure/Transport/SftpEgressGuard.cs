namespace Sankore.Modules.Integration.Infrastructure.Transport;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Shared.Infrastructure.Egress;

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
/// <b>The block list is <see cref="EgressAddressPolicy"/></b>, shared with every outbound HTTP
/// call. It used to be re-stated here range for range, because a module may not reference
/// another module's assembly; the shared infrastructure is where it belongs.
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
            if (!allowPrivate && EgressAddressPolicy.IsBlocked(literal))
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
            if (!EgressAddressPolicy.IsBlocked(address)) return new EgressTarget(address, port);
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
