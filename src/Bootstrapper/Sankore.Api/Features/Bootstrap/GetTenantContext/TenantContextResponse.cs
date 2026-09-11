namespace Sankore.Api.Features.Bootstrap.GetTenantContext;

/// <summary>
/// Public-safe tenant context returned to unauthenticated frontends.
/// Never includes RootUserEmail, BlockedReason, internal timestamps,
/// or any field that enables tenant enumeration.
/// </summary>
public record TenantContextResponse(
    Guid TenantId,
    string Name,
    bool IsActive,
    bool IsMaintenance,
    DateTimeOffset? TrialExpiresAt);