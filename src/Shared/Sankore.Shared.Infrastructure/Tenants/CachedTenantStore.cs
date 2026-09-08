using Microsoft.Extensions.Caching.Distributed;
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
    IDistributedCache cache,
    IOptions<TenantStoreOptions> options) : ITenantStore
{
    private static string CacheKey(Guid id) => $"tenant:exists:{id}";

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cached = await cache.GetStringAsync(CacheKey(tenantId), ct);
        if (cached is not null)
            return true;

        var exists = await inner.ExistsAsync(tenantId, ct);

        if (exists)
            await cache.SetStringAsync(CacheKey(tenantId), "1",
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = options.Value.CacheTtl
                }, ct);

        return exists;
    }
}
