namespace Sankore.Shared.Kernel;

/// <summary>
/// Resolves the current tenant (IMF/SFD) for the current request/operation.
/// Implemented in Sankore.Shared.Infrastructure by reading the JWT "tenant_id"
/// claim. Every module's DbContext depends on this abstraction (never on
/// HttpContext directly) to apply its global query filter, so the module
/// stays framework-agnostic and unit-testable.
/// </summary>
public interface ITenantContext
{
    Guid CurrentTenantId { get; }
    bool HasTenant { get; }
}

/// <summary>
/// Simple implementation used in tests and background jobs where there is
/// no HTTP request to read a claim from.
/// </summary>
public sealed class FixedTenantContext(Guid tenantId) : ITenantContext
{
    public Guid CurrentTenantId { get; } = tenantId;
    public bool HasTenant => true;
}
