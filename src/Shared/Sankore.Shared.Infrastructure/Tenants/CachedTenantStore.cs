using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Decorator that caches the full <see cref="TenantInfo"/> in Redis so repeated
/// requests for the same tenant do not hit Sankore.Admin on every call.
/// Negative results (unknown tenant) are NOT cached — the tenant may become active shortly after.
/// </summary>
internal sealed class CachedTenantStore(
    ITenantStore inner,
    IDistributedCache cache,
    IOptions<TenantStoreOptions> options) : ITenantStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string CacheKey(string key) => $"tenant:{key}";

    public Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default)
        => GetOrCache(tenantId.ToString(), () => inner.GetAsync(tenantId, ct), ct);

    public Task<TenantInfo?> GetByFqdnAsync(string fqdn, CancellationToken ct = default)
        => GetOrCache($"fqdn:{fqdn}", () => inner.GetByFqdnAsync(fqdn, ct), ct);

    private async Task<TenantInfo?> GetOrCache(string cacheId, Func<Task<TenantInfo?>> fetch, CancellationToken ct = default)
    {
        var cached = await cache.GetStringAsync(CacheKey(cacheId), ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<TenantInfo>(cached, JsonOpts);

        var tenant = await fetch();

        if (tenant is not null)
            await cache.SetStringAsync(
                CacheKey(cacheId),
                JsonSerializer.Serialize(tenant, JsonOpts),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = options.Value.CacheTtl
                }, ct);

        return tenant;
    }
}
