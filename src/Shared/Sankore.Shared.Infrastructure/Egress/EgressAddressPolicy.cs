namespace Sankore.Shared.Infrastructure.Egress;

using System.Net;
using System.Net.Sockets;

/// <summary>
/// The address ranges this process must never open a connection to on behalf of a tenant: a URL,
/// a host or a port that a tenant administrator typed, while the network position and the
/// credentials used to reach it are SANKORE's (SSRF).
///
/// <para>
/// <b>One list for the whole repository.</b> It used to exist twice — in Leads'
/// <c>SsrfSafeHandler</c> and in Integration's <c>SftpEgressGuard</c> — kept "range for range" by
/// hand, because a module may not reference another module's assembly. Both now call this one,
/// and so does every outbound webhook.
/// </para>
///
/// <para>
/// <b>Wider than the two copies were.</b> They missed the IPv6 unspecified address <c>::</c>
/// (which connects to the local host on Linux), multicast and reserved space in both families,
/// and the IPv6 spellings that carry an IPv4 address inside them (6to4, NAT64, IPv4-compatible,
/// Teredo): <c>2002:0a00:0001::</c> is <c>10.0.0.1</c> to a host with a 6to4 route.
/// </para>
/// </summary>
public static class EgressAddressPolicy
{
    /// <summary>True when <paramref name="address"/> lies in a range no outbound call may reach.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Loopback (127.0.0.0/8, ::1)
        if (IPAddress.IsLoopback(address)) return true;

        // ::ffff:10.0.0.1 is the same address as 10.0.0.1; mapping it back is what stops the v6
        // spelling of a private v4 target from skipping the v4 branch entirely.
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return addr.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(addr.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsBlockedV6(addr.GetAddressBytes()),
            // Anything that is neither v4 nor v6 is not something an HTTP or SFTP client should
            // be opening a socket to.
            _ => true,
        };
    }

    private static bool IsBlockedV4(byte[] b)
    {
        // 0.0.0.0/8 — "this network"; 0.0.0.0 itself connects to the local host.
        if (b[0] == 0) return true;

        // 10.0.0.0/8
        if (b[0] == 10) return true;

        // 100.64.0.0/10 (carrier-grade NAT)
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;

        // 127.0.0.0/8 is caught by IsLoopback; repeated for the bytes embedded in a v6 address.
        if (b[0] == 127) return true;

        // Link-local 169.254.0.0/16 — the cloud metadata service at 169.254.169.254 lives here,
        // and it is the single most valuable target an SSRF can reach.
        if (b[0] == 169 && b[1] == 254) return true;

        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;

        // 192.0.0.0/24 (IETF protocol assignments) and 192.0.2.0/24 (documentation)
        if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return true;

        // 192.88.99.0/24 (6to4 relay anycast)
        if (b[0] == 192 && b[1] == 88 && b[2] == 99) return true;

        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;

        // 198.18.0.0/15 (benchmarking)
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;

        // 198.51.100.0/24 and 203.0.113.0/24 (documentation)
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;

        // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved — 255.255.255.255 broadcast included.
        if (b[0] >= 224) return true;

        return false;
    }

    private static bool IsBlockedV6(byte[] b)
    {
        // :: (unspecified) and the deprecated IPv4-compatible ::a.b.c.d — the first twelve bytes
        // are zero. ::1 has already been refused as loopback.
        if (b.Take(12).All(x => x == 0))
            return b.Skip(12).All(x => x == 0) || IsBlockedV4(b[12..16]);

        // ff00::/8 multicast
        if (b[0] == 0xff) return true;

        // fe80::/10 link-local and fec0::/10 site-local (deprecated, still routed by some stacks)
        if (b[0] == 0xfe && (b[1] & 0x80) == 0x80) return true;

        // fc00::/7 unique local
        if ((b[0] & 0xfe) == 0xfc) return true;

        // 64:ff9b::/96 well-known NAT64 prefix — the IPv4 target is the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            return IsBlockedV4(b[12..16]);

        // 64:ff9b:1::/48 local-use NAT64: translated to whatever the operator's network decides.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01) return true;

        // 100::/64 discard-only
        if (b[0] == 0x01 && b[1] == 0x00 && b.Skip(2).Take(6).All(x => x == 0)) return true;

        // 2001::/32 Teredo — the IPv4 server and client are obfuscated inside it; refused outright.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true;

        // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true;

        // 2002::/16 6to4 — the IPv4 address is bytes 2 to 5.
        if (b[0] == 0x20 && b[1] == 0x02) return IsBlockedV4(b[2..6]);

        return false;
    }
}
