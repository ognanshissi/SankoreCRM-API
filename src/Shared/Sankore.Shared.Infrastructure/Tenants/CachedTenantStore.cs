using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Decorator that caches tenant lookups in Redis so repeated requests do not hit Sankore.Admin on
/// every call. Negative results (unknown tenant) are NOT cached — the tenant may exist shortly
/// after.
///
/// <para>
/// <b>Two caches with two lifetimes, and only one of them holds a STATUS.</b> The FQDN cache maps
/// an origin to a tenant id and nothing else: that mapping is stable, so it keeps the long
/// <see cref="TenantStoreOptions.FqdnCacheTtl"/>. Everything that can change — active, maintenance,
/// trial — is read through the id cache and its short <see cref="TenantStoreOptions.CacheTtl"/>.
/// </para>
///
/// <para>
/// The FQDN cache used to store the WHOLE <see cref="TenantInfo"/> for 24 hours. The middleware
/// only took the id from it, but <c>GetTenantContext</c> handed its <c>IsActive</c> to the
/// front-end, so a tenant deactivated in the admin kept booting as active for a day — and with the
/// id cache at 5 minutes, its API calls kept succeeding for that long too.
/// </para>
/// </summary>
internal sealed class CachedTenantStore(
    ITenantStore inner,
    IDistributedCache cache,
    IOptions<TenantStoreOptions> options) : ITenantStore, ITenantCacheInvalidator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string IdKey(Guid tenantId) => $"tenant:id:{tenantId}";

    // A NEW key prefix, not "tenant:fqdn:": entries written by the previous version hold a whole
    // TenantInfo, and reading one as a bare id would fail until it expired a day later.
    private static string FqdnKey(string fqdn) => $"tenant:fqdn-id:{fqdn}";

    public async Task<TenantInfo?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cached = await cache.GetStringAsync(IdKey(tenantId), ct);
        if (cached is not null)
        {
            var hit = JsonSerializer.Deserialize<TenantInfo>(cached, JsonOpts);
            TenantTwins.Remember(hit);
            return hit;
        }

        var tenant = await inner.GetAsync(tenantId, ct);
        if (tenant is not null)
            await cache.SetStringAsync(
                IdKey(tenantId),
                JsonSerializer.Serialize(tenant, JsonOpts),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.CacheTtl },
                ct);

        return tenant;
    }

    public async Task<TenantInfo?> GetByFqdnAsync(string fqdn, CancellationToken ct = default)
    {
        var cachedId = await cache.GetStringAsync(FqdnKey(fqdn), ct);
        if (cachedId is not null && Guid.TryParse(cachedId, out var id))
            return await GetAsync(id, ct);

        var tenant = await inner.GetByFqdnAsync(fqdn, ct);
        if (tenant is null)
            return null;

        await cache.SetStringAsync(
            FqdnKey(fqdn),
            tenant.Id.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.FqdnCacheTtl },
            ct);

        // Seeds the id cache with what was just read, so the request that follows a cold FQDN
        // lookup does not pay a second round-trip for the same tenant.
        await cache.SetStringAsync(
            IdKey(tenant.Id),
            JsonSerializer.Serialize(tenant, JsonOpts),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.CacheTtl },
            ct);

        return tenant;
    }

    public Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync(CancellationToken ct = default)
        => inner.GetAllActiveAsync(ct);   // the inner store remembers the twins it lists

    /// <summary>
    /// Only the id entry: it is the one holding a status. The FQDN entry maps an origin to an
    /// id, which a module change does not move.
    /// </summary>
    public Task InvalidateAsync(Guid tenantId, CancellationToken ct = default)
        => cache.RemoveAsync(IdKey(tenantId), ct);
}
