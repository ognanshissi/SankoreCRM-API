using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Domain;

public class UserLoginLocation
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }

    public GeoPoint? Location { get; private set; }

    public DateTimeOffset OccuredAt { get; private set; }

    /// <summary>
    /// Address the request came from. Nullable: a request can reach the API through a proxy that
    /// strips it, and a login history that refuses to record the login because it could not read
    /// an IP would be worse than one with a gap.
    /// </summary>
    public string? IpAddress { get; private set; }

    /// <summary>
    /// The User-Agent header as received, truncated. Kept raw ON PURPOSE: the parsed fields below
    /// are a best-effort reading of a format that changes constantly, and a security log must let
    /// an investigator re-read the original rather than trust last year's parser.
    /// </summary>
    public string? UserAgent { get; private set; }

    public string? Browser { get; private set; }
    public string? BrowserVersion { get; private set; }
    public LoginPlatform Platform { get; private set; } = LoginPlatform.Unknown;
    public LoginClientKind ClientKind { get; private set; } = LoginClientKind.Unknown;

    private UserLoginLocation() { }

    public static UserLoginLocation Create(
        Guid tenantId,
        Guid userId,
        GeoPoint? location,
        string? ipAddress,
        UserAgentInfo? userAgent)
    {
        return new UserLoginLocation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Location = location,
            OccuredAt = DateTimeOffset.UtcNow,
            IpAddress = Trim(ipAddress, MaxIpLength),
            UserAgent = userAgent?.Raw,
            Browser = Trim(userAgent?.Browser, MaxBrowserLength),
            BrowserVersion = Trim(userAgent?.BrowserVersion, MaxBrowserVersionLength),
            Platform = userAgent?.Platform ?? LoginPlatform.Unknown,
            ClientKind = userAgent?.ClientKind ?? LoginClientKind.Unknown,
        };
    }

    // Both values come straight from the network and are attacker-controlled; the column widths
    // are the guard, and truncating here keeps a malformed header from failing the login itself.
    internal const int MaxIpLength = 45;            // an IPv6 address with an IPv4 tail
    internal const int MaxBrowserLength = 60;
    internal const int MaxBrowserVersionLength = 30;

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
