namespace Sankore.Modules.Administration.Infrastructure;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// The 5-minute cache BNF-B-06 asks for over the product catalogue, and the single place the keys
/// are spelled.
///
/// <para>
/// <b>Why it is a service and not inline in the facade.</b> The facade READS and the product
/// command handlers INVALIDATE. Those are four call sites for one key format, and a cache whose
/// writer and reader disagree about a key is a cache that never invalidates — the operator changes
/// a policy, nothing happens, and the only symptom is a five-minute delay that looks like
/// flakiness.
/// </para>
///
/// <para>
/// <b><see cref="IDistributedCache"/> is OPTIONAL.</b> Redis is provisioned (Aspire
/// <c>AddRedis("redis")</c>, consumed by M08 and M13) but it is absent in unit tests, so every
/// path falls back to the query. Resolved with <c>GetService</c> rather than
/// <c>GetRequiredService</c>, the idiom M13's <c>AgentCapacityService</c> already uses.
/// </para>
///
/// <para>
/// <b>Why per-line and per-tenant-catalogue keys rather than one blob.</b> A policy read happens
/// per lead and names one line; label resolution needs the whole active catalogue of a tenant.
/// Two shapes, two keys — and an invalidation drops both, because a renamed or retired product
/// changes what the catalogue resolves to as well as what the line's policy says.
/// </para>
/// </summary>
internal sealed class ProductCatalogCache(IDistributedCache? cache = null)
{
    private static readonly DistributedCacheEntryOptions Options = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string PolicyKey(Guid tenantId, Guid productLineId)
        => $"m12:product-policy:{tenantId}:{productLineId}";

    internal static string CatalogueKey(Guid tenantId)
        => $"m12:product-catalogue:{tenantId}";

    /// <summary>
    /// The remote-KYC policy of one line (KRM-B-01). Its OWN key rather than a member of the
    /// beneficiary-policy payload: the two are read by different callers at different moments — a
    /// lead's completeness gate versus a conversion's link decision — and sharing one entry would
    /// make every beneficiary-policy read deserialise a payload it has no use for.
    /// </summary>
    internal static string RemoteKycPolicyKey(Guid tenantId, Guid productLineId)
        => $"m12:product-remote-kyc:{tenantId}:{productLineId}";

    internal async Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
    {
        if (cache is null)
            return null;

        try
        {
            var hit = await cache.GetStringAsync(key, ct);
            return hit is null ? null : JsonSerializer.Deserialize<T>(hit, Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cache is an optimisation. Redis being unreachable, or holding a payload written by
            // an older shape of this record, must cost a query — not the request.
            return null;
        }
    }

    internal async Task SetAsync<T>(string key, T value, CancellationToken ct)
    {
        if (cache is null)
            return;

        try
        {
            await cache.SetStringAsync(key, JsonSerializer.Serialize(value, Json), Options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning: failing to populate the cache is not a failed read.
        }
    }

    /// <summary>
    /// Drops everything this tenant's catalogue answers for. Called by every command that changes
    /// a product — create, update, policy, CBS link, retire, delete.
    /// </summary>
    /// <remarks>
    /// The LINE key is dropped by id and the CATALOGUE key wholesale, because a product's code or
    /// name changing moves what a label resolves to. Nothing here enumerates other tenants' keys:
    /// <see cref="IDistributedCache"/> has no prefix scan, and a tenant's own two keys are exactly
    /// what changed.
    /// </remarks>
    internal async Task InvalidateAsync(Guid tenantId, Guid? productLineId, CancellationToken ct)
    {
        if (cache is null)
            return;

        try
        {
            if (productLineId is { } lineId)
            {
                await cache.RemoveAsync(PolicyKey(tenantId, lineId), ct);
                await cache.RemoveAsync(RemoteKycPolicyKey(tenantId, lineId), ct);
            }

            await cache.RemoveAsync(CatalogueKey(tenantId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowing this one is the uncomfortable case: a failed invalidation means a stale
            // policy for up to five minutes. It is still better than failing the administrator's
            // write, which has already been committed by the time this runs — and the TTL is the
            // backstop that makes the window bounded rather than permanent.
        }
    }
}
