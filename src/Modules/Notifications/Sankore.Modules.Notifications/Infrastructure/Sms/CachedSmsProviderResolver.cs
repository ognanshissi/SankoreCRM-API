namespace Sankore.Modules.Notifications.Infrastructure.Sms;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Cache-aside, the same three steps as <c>CachedEmailProviderResolver</c>: hit the cache, else
/// ask M12 and write it, else answer <see cref="ResolvedSmsProvider.None"/>.
///
/// <para>
/// Invalidated synchronously by M12's own handlers and asynchronously by
/// <c>TenantSmsSettingsChangedConsumer</c> — the second because the first is best-effort and a
/// settings change that did not reach the cache would take fifteen minutes to bite, which reads
/// as flakiness rather than as a cache.
/// </para>
/// </summary>
internal sealed class CachedSmsProviderResolver(
    IAdministrationModule admin,
    IDistributedCache cache,
    ILogger<CachedSmsProviderResolver> logger)
    : ISmsProviderResolver
{
    /// <summary>
    /// The same fifteen minutes the email resolver uses. Worth noting what it costs here: a
    /// tenant that has just configured its channel can wait up to this long if BOTH invalidation
    /// paths fail, and the symptom is "no SMS channel" rather than an error.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ResolvedSmsProvider> ResolveAsync(Guid tenantId, CancellationToken ct)
    {
        var key = CacheKey(tenantId);

        var cached = await cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<ResolvedSmsProvider>(cached, JsonOpts)!;

        logger.LogDebug("SMS provider cache miss for tenant {TenantId}", tenantId);

        var config = await admin.GetSmsConfigAsync(tenantId, ct);

        var provider = config is null
            ? ResolvedSmsProvider.None
            : new ResolvedSmsProvider(
                config.ProviderType,
                config.SenderId,
                config.AccountSid,
                config.HasCredential,
                // Carried from M12 rather than recomputed: the four conditions that make a channel
                // sendable live on the entity, and a second derivation here is a second chance to
                // get one of them wrong.
                config.CanSend);

        await cache.SetStringAsync(
            key,
            JsonSerializer.Serialize(provider, JsonOpts),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl },
            ct);

        return provider;
    }

    /// <summary>
    /// Must agree with <c>UpdateSmsSettingsHandler.CacheKey</c>. A reader and a writer that
    /// disagree about a key never invalidate, and the only symptom is a delay that looks like
    /// flakiness — which is why both spell it in one place each and both are named.
    /// </summary>
    internal static string CacheKey(Guid tenantId) => $"notifications:sms-provider:{tenantId}";
}
