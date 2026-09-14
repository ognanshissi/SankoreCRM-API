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
    DateTimeOffset? BlockedAt,
    /// <summary>
    /// BCP-47 language tag used when a user has no personal preference (e.g. "fr", "en").
    /// Defaults to "fr". Missing in older cached records falls back to "fr" via the default value.
    /// </summary>
    string DefaultLanguage = "fr");
