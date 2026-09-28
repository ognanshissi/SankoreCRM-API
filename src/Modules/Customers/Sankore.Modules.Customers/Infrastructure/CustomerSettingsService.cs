namespace Sankore.Modules.Customers.Infrastructure;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Settings reader/writer backed by <c>customers.customer_settings</c>.
///
/// Caching is deliberately a process-local <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// with a short TTL rather than IMemoryCache or Redis: these values are read on almost
/// every handler (minimum age, group sizes, thresholds) but change a few times a year,
/// and the module must not take a caching dependency for it. A 60-second TTL means the
/// worst case after an operator changes a parameter on another node is one stale minute,
/// while the local node sees it immediately (<see cref="SetAsync"/> evicts its own entry).
///
/// Every read goes through <c>IgnoreQueryFilters()</c> plus an explicit tenant predicate:
/// background jobs have no ambient tenant, and silently returning another tenant's
/// parameters would be far worse than returning none.
/// </summary>
internal sealed class CustomerSettingsService(CustomersDbContext db) : ICustomerSettings
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<(Guid TenantId, string Key), CacheEntry> Cache = new();

    private readonly record struct CacheEntry(string Value, DateTimeOffset ExpiresAt);

    public async Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (Cache.TryGetValue((tenantId, key), out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Value;

        var stored = await db.CustomerSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        var value = stored ?? Default(key);
        Cache[(tenantId, key)] = new CacheEntry(value, DateTimeOffset.UtcNow.Add(CacheTtl));
        return value;
    }

    public async Task<int> GetIntAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var raw = await GetStringAsync(tenantId, key, ct);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        // A stored value that no longer parses (hand-edited row, changed type) must not
        // take the whole feature down: fall back to the declared default instead.
        return int.Parse(Default(key), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public async Task<bool> GetBoolAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var raw = await GetStringAsync(tenantId, key, ct);
        if (bool.TryParse(raw, out var parsed))
            return parsed;

        // Tolerate the numeric spellings an operator or an import may produce.
        if (raw is "1") return true;
        if (raw is "0") return false;

        return bool.Parse(Default(key));
    }

    public async Task<decimal> GetDecimalAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var raw = await GetStringAsync(tenantId, key, ct);
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return decimal.Parse(Default(key), NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid tenantId, CancellationToken ct)
    {
        var stored = await db.CustomerSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        // Start from the declared defaults so the caller always receives the complete
        // set of keys, then let the tenant's own rows win.
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declared in CustomerSettingKeys.Defaults)
            all[declared.Key] = declared.Value;
        foreach (var row in stored)
            all[row.Key] = row.Value;

        var expiresAt = DateTimeOffset.UtcNow.Add(CacheTtl);
        foreach (var pair in all)
            Cache[(tenantId, pair.Key)] = new CacheEntry(pair.Value, expiresAt);

        return all;
    }

    public async Task<Result> SetAsync(Guid tenantId, string key, string value, Guid actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        // Settings are a closed list: an unknown key is a caller bug, not a new setting.
        if (!CustomerSettingKeys.DefaultsByKey.TryGetValue(key, out var declared))
            return Result.Fail(CustomerErrors.SettingUnknown);

        var existing = await db.CustomerSettings
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Key == key, ct);

        if (existing is null)
        {
            // The tenant was running on the compiled-in default so far; materialise
            // the row on first write rather than requiring the seeder to have run.
            var created = CustomerSetting.Create(
                tenantId, key, value, declared.ValueType, declared.Description);
            created.SetValue(value, actor);
            db.CustomerSettings.Add(created);
        }
        else
        {
            existing.SetValue(value, actor);
        }

        await db.SaveChangesAsync(ct);

        // Evict rather than overwrite: the next read re-fetches and re-stamps the TTL,
        // which also covers the case where SaveChanges was rolled back by the ambient
        // TransactionScope after this point.
        Cache.TryRemove((tenantId, key), out _);
        return Result.Ok();
    }

    private static string Default(string key)
        => CustomerSettingKeys.DefaultsByKey.TryGetValue(key, out var declared)
            ? declared.Value
            // Not a business failure: asking for a key that was never declared means the
            // calling code referenced a setting that does not exist.
            : throw new InvalidOperationException(
                $"Unknown customer setting key '{key}'. Declare it in CustomerSettingKeys.Defaults.");
}

