namespace Sankore.Shared.Infrastructure.Egress;

/// <summary>
/// Platform-level policy for the HTTP calls this process makes to a URL a tenant configured,
/// bound from <c>Egress:Http</c>. Never a tenant setting: the tenant chooses the target, the
/// network position is SANKORE's.
/// </summary>
public sealed class OutboundHttpOptions
{
    public const string SectionName = "Egress:Http";

    /// <summary>
    /// The only ports such a call may reach. HTTPS on 443 and 8443 by default — a closed list,
    /// because a public address answering on 25, 6379 or 5432 is still somebody's SMTP relay,
    /// cache or database, and a request we send there is a request we are responsible for.
    /// </summary>
    public int[] AllowedPorts { get; set; } = [443, 8443];

    /// <summary>Time allowed to open the TCP connection, TLS excluded.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
