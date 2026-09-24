namespace Sankore.Modules.Leads.Infrastructure.SsrfProtection;

using System.Net;
using System.Net.Sockets;
using Sankore.Shared.Kernel;

/// <summary>
/// Prevents SSRF attacks by validating outbound connections at the socket level.
/// - Only HTTPS is allowed.
/// - Private, loopback, link-local, and cloud metadata IPs are blocked.
/// - DNS rebinding is defeated by checking the resolved IP in ConnectCallback.
/// - Redirects are not followed automatically (max 1 same-host redirect).
/// - Error messages never reveal the resolved address.
/// </summary>
public static class SsrfSafeHandler
{
    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> with SSRF protections applied.
    /// </summary>
    public static SocketsHttpHandler Create()
    {
        var handler = new SocketsHttpHandler
        {
            // No automatic redirects — we handle manually
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                // Enforce HTTPS only
                if (!context.DnsEndPoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    && context.InitialRequestMessage.RequestUri?.Scheme != "https")
                {
                    throw new DomainException("OutboundAddressNotAllowed");
                }

                // Resolve DNS and validate each address
                var addresses = await Dns.GetHostAddressesAsync(
                    context.DnsEndPoint.Host, context.DnsEndPoint.AddressFamily, ct);

                if (addresses.Length == 0)
                    throw new DomainException("OutboundAddressNotAllowed");

                // Find the first safe address
                IPAddress? safeAddress = null;
                foreach (var addr in addresses)
                {
                    if (!IsBlockedAddress(addr))
                    {
                        safeAddress = addr;
                        break;
                    }
                }

                if (safeAddress is null)
                    throw new DomainException("OutboundAddressNotAllowed");

                // Connect to the validated IP
                var socket = new Socket(safeAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(safeAddress, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return handler;
    }

    /// <summary>
    /// Validates a URL before it is stored in configuration.
    /// Returns the error reason or null if valid.
    /// </summary>
    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "URL is required.";

        // Allow template variables in URL
        var testUrl = url
            .Replace("{{since}}", "2026-01-01T00:00:00Z")
            .Replace("{{cursor}}", "abc")
            .Replace("{{page}}", "1")
            .Replace("{{offset}}", "0")
            .Replace("{{pageSize}}", "100");

        if (!Uri.TryCreate(testUrl, UriKind.Absolute, out var uri))
            return "Invalid URL format.";

        if (uri.Scheme != "https")
            return "Only HTTPS URLs are allowed.";

        return null;
    }

    /// <summary>
    /// Checks if an IP address belongs to a blocked range:
    /// private (RFC 1918), loopback, link-local, cloud metadata.
    /// </summary>
    private static bool IsBlockedAddress(IPAddress address)
    {
        // Loopback (127.0.0.0/8, ::1)
        if (IPAddress.IsLoopback(address))
            return true;

        // Map IPv6-mapped IPv4 to IPv4 for consistent checking
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

            // Link-local 169.254.0.0/16 (includes cloud metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 0.0.0.0/8
            if (bytes[0] == 0) return true;

            // 100.64.0.0/10 (Carrier-grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 link-local (fe80::/10)
            var bytes = addr.GetAddressBytes();
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true;

            // IPv6 unique local (fc00::/7)
            if ((bytes[0] & 0xfe) == 0xfc) return true;
        }

        return false;
    }
}
