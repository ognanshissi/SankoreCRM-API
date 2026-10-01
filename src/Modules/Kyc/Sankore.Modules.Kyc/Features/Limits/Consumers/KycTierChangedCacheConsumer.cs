namespace Sankore.Modules.Kyc.Features.Limits.Consumers;

using MassTransit;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.PublicApi;

/// <summary>
/// Drops a customer's cached ceilings the moment their tier moves.
///
/// The five-minute TTL on <c>KycModuleFacade.GetLimitsAsync</c> is a floor, not the mechanism. A
/// downgrade — an expired identity document, a lapsed review — must bite at once: for up to five
/// minutes otherwise, a customer who has just been capped would still be measured against the
/// uncapped ceilings, and every operation accepted in that window is one a regulator would ask
/// about.
///
/// The reverse matters less but is the same code: an upgrade should not keep refusing operations
/// the customer is now entitled to.
/// </summary>
public sealed class KycTierChangedCacheConsumer(
    IDistributedCache cache,
    ILogger<KycTierChangedCacheConsumer> logger)
    : IConsumer<KycTierChangedEvent>
{
    public async Task Consume(ConsumeContext<KycTierChangedEvent> context)
    {
        var evt = context.Message;

        // SYSTEM placeholder: nothing to invalidate for a tenant that does not exist.
        if (evt.TenantId == Guid.Empty) return;

        // The key carries the tenant's cache generation, so it has to be resolved rather than
        // rebuilt from the ids alone — otherwise this removes a key nothing ever wrote.
        var generation = await KycModuleFacade.ReadLimitsGenerationAsync(
            cache, evt.TenantId, context.CancellationToken);

        await cache.RemoveAsync(
            KycModuleFacade.LimitsCacheKey(evt.TenantId, evt.CustomerEntityId, generation),
            context.CancellationToken);

        logger.LogInformation(
            "KYC limits cache dropped for customer {CustomerId}: {Previous} → {Current}",
            evt.CustomerEntityId, evt.PreviousTier, evt.CurrentTier);
    }
}
