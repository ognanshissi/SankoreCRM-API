namespace Sankore.Modules.Notifications.Infrastructure.Consumers;

using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Infrastructure.Sms;

/// <summary>
/// Drops the SMS provider-resolution cache when M12 publishes a change through its outbox.
///
/// <para>
/// Its own consumer beside the email one, keyed on its own event, so an email settings change does
/// not drop the SMS entry and the reverse. The synchronous removal in M12's handler is
/// best-effort; this is the durable path, and it matters more here than on the email side: a
/// tenant that has just configured its SMS channel and sees nothing go out for fifteen minutes
/// reads that as the feature not working.
/// </para>
/// </summary>
internal sealed class TenantSmsSettingsChangedConsumer(
    IDistributedCache cache,
    ILogger<TenantSmsSettingsChangedConsumer> logger)
    : IConsumer<TenantSmsSettingsChangedEvent>
{
    public async Task Consume(ConsumeContext<TenantSmsSettingsChangedEvent> context)
    {
        var tenantId = context.Message.TenantId;

        if (tenantId == Guid.Empty) return;

        await cache.RemoveAsync(
            CachedSmsProviderResolver.CacheKey(tenantId), context.CancellationToken);

        logger.LogInformation(
            "SMS provider cache invalidated for tenant {TenantId} via integration event", tenantId);
    }
}
