namespace Sankore.Shared.Kernel;

/// <summary>
/// Tenant data fetched from the external tenant registry (Sankore.Admin).
/// Cached in Redis; used by TenantResolutionMiddleware to gate every request.
/// </summary>
public sealed record TenantInfo(
    Guid Id,
    string Name,
    string Fqdn,
    bool IsActive,
    bool IsMaintenance,
    DateTimeOffset? TrialExpiresAt,
    DateTimeOffset? BlockedAt);
