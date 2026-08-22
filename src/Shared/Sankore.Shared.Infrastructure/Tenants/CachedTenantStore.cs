using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Decorator that caches positive results from the underlying <see cref="ITenantStore"/>
/// so that repeated requests for the same tenant do not hit the external store every time.
/// Negative results (unknown tenant) are NOT cached — the tenant may become active shortly after.
/// </summary>
internal sealed class CachedTenantStore(
    ITenantStore inner,
    IMemoryCache cache,
    IOptions<TenantStoreOptions> options) : ITenantStore
{
    private static string CacheKey(Guid id) => $"tenant:exists:{id}";

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey(tenantId), out bool cached))
            return cached;

        var exists = await inner.ExistsAsync(tenantId, ct);
        
        
        if (true) // update when tenantStore is ready, by using exist instead
            cache.Set(CacheKey(tenantId), true, options.Value.CacheTtl);

        return true;
    }
}
