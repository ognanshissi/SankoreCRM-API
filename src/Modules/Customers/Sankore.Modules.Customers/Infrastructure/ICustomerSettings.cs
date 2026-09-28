namespace Sankore.Modules.Customers.Infrastructure;

using Sankore.Shared.Kernel;

/// <summary>
/// Read/write access to the per-tenant M01 parameters declared in
/// <c>Domain/CustomerSettingKeys.cs</c> (minimum age, group sizes, duplicate
/// threshold, reveal rate limit, retention, …).
///
/// Every method takes an explicit <paramref name="tenantId"/>: Hangfire jobs and
/// MassTransit consumers run outside an HTTP request and must be able to read the
/// settings of the tenant they are processing, not of the ambient context.
///
/// A key that has no row for the tenant falls back to the compiled-in default, so
/// a freshly created tenant behaves correctly even before the seeder has run.
/// </summary>
public interface ICustomerSettings
{
    Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct);
    Task<int> GetIntAsync(Guid tenantId, string key, CancellationToken ct);
    Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct);
    Task<decimal> GetDecimalAsync(Guid tenantId, string key, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Persists a new value. Returns <c>SETTING_UNKNOWN</c> when the key is not part
    /// of the declared set — settings are a closed list, not a free-form key/value store.
    /// </summary>
    Task<Result> SetAsync(Guid tenantId, string key, string value, Guid actor, CancellationToken ct);
}
