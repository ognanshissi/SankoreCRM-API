namespace Sankore.Shared.Infrastructure.Tenants;

public sealed class TenantStoreOptions
{
    public const string Section = "TenantStore";

    /// <summary>Base URL of the external tenant registry, e.g. https://tenants.internal.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>How long a positive existence result is cached. Defaults to 5 minutes.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}
