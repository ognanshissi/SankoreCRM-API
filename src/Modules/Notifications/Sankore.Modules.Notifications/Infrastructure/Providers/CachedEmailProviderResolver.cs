namespace Sankore.Modules.Notifications.Infrastructure.Providers;

using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// Cache-aside resolver:
///   1. Hit Redis → return immediately.
///   2. Miss → call IAdministrationModule, write to cache, return.
///   3. No custom config in Administration → return platform default (SES).
/// Cache is invalidated synchronously by the Administration handlers on change
/// and asynchronously via TenantNotificationSettingsChangedConsumer.
/// </summary>
internal sealed class CachedEmailProviderResolver(
    IAdministrationModule admin,
    IDistributedCache cache,
    ILogger<CachedEmailProviderResolver> logger)
    : IEmailProviderResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly ResolvedEmailProvider PlatformDefault =
        new("Default", true, null, null, null, null, null);

    public async Task<ResolvedEmailProvider> ResolveAsync(Guid tenantId, CancellationToken ct)
    {
        var key = CacheKey(tenantId);

        var cached = await cache.GetStringAsync(key, ct);
        if (cached is not null)
        {
            logger.LogDebug("Provider cache hit for tenant {TenantId}", tenantId);
            return JsonSerializer.Deserialize<ResolvedEmailProvider>(cached, JsonOpts)!;
        }

        logger.LogDebug("Provider cache miss for tenant {TenantId} — loading from Administration", tenantId);

        var config = await admin.GetNotificationConfigAsync(tenantId, ct);

        var provider = config is null
            ? PlatformDefault
            : new ResolvedEmailProvider(
                config.ProviderType,
                config.UseDefaultPlatformProvider,
                config.FromEmail,
                config.FromName,
                config.ReplyToEmail,
                config.SendingDomain,
                config.CredentialVaultPath);

        var serialized = JsonSerializer.Serialize(provider, JsonOpts);
        await cache.SetStringAsync(key, serialized,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheTtl
            }, ct);

        return provider;
    }

    internal static string CacheKey(Guid tenantId) => $"notifications:provider:{tenantId}";
}
