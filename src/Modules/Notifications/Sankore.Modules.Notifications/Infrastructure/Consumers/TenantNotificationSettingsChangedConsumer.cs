namespace Sankore.Modules.Notifications.Infrastructure.Consumers;

using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Infrastructure.Providers;

/// <summary>
/// Invalidates the Redis provider-resolution cache when Administration
/// publishes a TenantNotificationSettingsChangedEvent via its outbox.
/// Ensures cache coherence beyond the synchronous removal done in-process.
/// </summary>
internal sealed class TenantNotificationSettingsChangedConsumer(
    IDistributedCache cache,
    ILogger<TenantNotificationSettingsChangedConsumer> logger)
    : IConsumer<TenantNotificationSettingsChangedEvent>
{
    public async Task Consume(ConsumeContext<TenantNotificationSettingsChangedEvent> context)
    {
        var tenantId = context.Message.TenantId;
        await cache.RemoveAsync(CachedEmailProviderResolver.CacheKey(tenantId), context.CancellationToken);
        logger.LogInformation(
            "Notification provider cache invalidated for tenant {TenantId} via integration event",
            tenantId);
    }
}
