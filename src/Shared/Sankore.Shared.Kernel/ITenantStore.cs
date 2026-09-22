namespace Sankore.Shared.Kernel;

/// <summary>
/// Port for retrieving tenant data from the external tenant registry.
/// Implemented by infrastructure (HTTP call to Sankore.Admin) so the Kernel
/// stays free of any I/O dependency.
/// </summary>
public interface ITenantStore
{
    /// <summary>
    /// Returns the full tenant record, or <c>null</c> if the tenant is unknown.
    /// Callers should treat an inactive or maintenance tenant as appropriate.
    /// </summary>
    Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default);
    
    Task<TenantInfo?> GetByFqdnAsync(string fqdn, CancellationToken ct = default);

    /// <summary>
    /// Returns all active tenants. Used by Hangfire job registration
    /// to create per-tenant recurring jobs at startup.
    /// </summary>
    Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync(CancellationToken ct = default);
}
