namespace Sankore.Shared.Infrastructure.Tenants;

public sealed class TenantStoreOptions
{
    public const string Section = "TenantStore";

    /// <summary>Base URL of the external tenant registry, e.g. https://tenants.internal.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>How long a tenant-by-ID result is cached. Defaults to 5 minutes.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an FQDN→TenantId mapping is cached.
    /// FQDN mappings are stable (domain aliases rarely change), so a much longer
    /// TTL is safe and avoids hammering Sankore.Admin on every unauthenticated request.
    /// Defaults to 24 hours.
    /// </summary>
    public TimeSpan FqdnCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Shared API key sent as X-Api-Key header for service-to-service authentication
    /// with SankoreAdmin. Required in production.
    /// </summary>
    public string? ApiKey { get; set; }
}
