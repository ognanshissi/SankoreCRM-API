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
    string DefaultLanguage = "fr",
    /// <summary>
    /// Codes of the paid modules in force (<see cref="TenantModules"/>), from the registry's
    /// <c>TenantResponse.ActiveModules</c>. Empty means the socle alone. <c>null</c> means
    /// UNKNOWN — a record cached before the field existed, or a registry that did not send it —
    /// and reads as « every module » through <see cref="HasModule"/>: a module is a commercial
    /// boundary, not a compliance rule, and in doubt the tenant keeps what it had.
    /// </summary>
    IReadOnlyList<string>? ActiveModules = null,
    /// <summary>
    /// Modules deactivated and not active again (<c>TenantResponse.ReadOnlyModules</c>): readable,
    /// not writable. <c>null</c> on an older record reads as none.
    /// </summary>
    IReadOnlyList<string>? ReadOnlyModules = null)
{
    /// <summary>True when <paramref name="module"/> is in force, or when the list is unknown.</summary>
    public bool HasModule(string module) => ActiveModules is null || ActiveModules.Contains(module);

    /// <summary>True when the module's data may be CONSULTED: in force, or deactivated.</summary>
    public bool CanRead(string module) => HasModule(module) || ReadOnlyModules?.Contains(module) == true;
}
