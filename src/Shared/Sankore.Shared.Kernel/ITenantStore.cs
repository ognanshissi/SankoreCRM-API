namespace Sankore.Shared.Kernel;

/// <summary>
/// Port for verifying whether a tenant identifier refers to a real, active
/// tenant. Implemented by infrastructure (HTTP call to an external tenant
/// registry) so the Kernel stays free of any I/O dependency.
/// </summary>
public interface ITenantStore
{
    /// <summary>Returns <c>true</c> if the tenant is known and active.</summary>
    Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default);
}
