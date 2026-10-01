namespace Sankore.Modules.Kyc.Infrastructure.Settings;

using Sankore.Shared.Kernel;

/// <summary>
/// Read/write access to the per-tenant M02 parameters declared in
/// <c>Domain/KycSettingKeys.cs</c> — simplified-tier ceilings, review periodicity, grace period,
/// face-match attempts.
///
/// Every method takes an explicit <paramref name="tenantId"/>: the review orchestrator and the
/// MassTransit consumers run outside an HTTP request and must read the settings of the tenant they
/// are processing, not of the ambient context.
///
/// A key with no row for the tenant falls back to the compiled-in default, so a freshly created
/// tenant behaves correctly even before the seeder has run — and a ceiling is never accidentally
/// zero because a row is missing.
/// </summary>
public interface IKycSettings
{
    Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct);
    Task<int> GetIntAsync(Guid tenantId, string key, CancellationToken ct);
    Task<decimal> GetDecimalAsync(Guid tenantId, string key, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Persists a new value. Returns <c>KYC_SETTING_UNKNOWN</c> when the key is not part of the
    /// declared set, and <c>KYC_SETTING_INVALID_VALUE</c> when it does not parse as its declared
    /// type — settings are a closed, typed list, not a free-form key/value store.
    /// </summary>
    Task<Result> SetAsync(Guid tenantId, string key, string value, Guid actor, CancellationToken ct);
}
