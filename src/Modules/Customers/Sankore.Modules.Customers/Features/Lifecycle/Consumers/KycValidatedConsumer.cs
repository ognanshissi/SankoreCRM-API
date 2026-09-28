namespace Sankore.Modules.Customers.Features.Lifecycle.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Applies an approved KYC file to the client record (US-M01-BE-13).
///
/// Two behaviours, decided by the aggregate:
/// <list type="bullet">
/// <item><c>PendingKyc</c> → <c>Active</c>, and <see cref="ClientActivatedEvent"/>
///       is published so the rest of the platform learns the client may transact;</item>
/// <item><c>Suspended</c> or <c>Archived</c> → ONLY <c>KycStatus</c> is refreshed.
///       A compliance suspension or an archive is a human decision that an
///       automated KYC approval must never override, so no status change and no
///       activation event.</item>
/// </list>
///
/// Runs outside any HTTP request: there is no ITenantContext to trust, hence
/// <c>IgnoreQueryFilters()</c> plus an explicit TenantId predicate taken from the
/// event payload.
/// </summary>
public sealed class KycValidatedConsumer(
    CustomersDbContext db,
    IInboxGuard inbox,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    ILogger<KycValidatedConsumer> logger)
    : IConsumer<KycValidatedEvent>
{
    public async Task Consume(ConsumeContext<KycValidatedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        // Delivery is at-least-once. The inbox guard is the FIRST thing that runs:
        // a redelivery loses on the inbox primary key and returns immediately, so a
        // replay can never append a second history line nor re-publish the
        // activation event.
        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId, evt.TenantId, nameof(KycValidatedEvent), ct))
            return;

        // SYSTEM identity for the whole flow: the audit trail and the status-history
        // line must name the system account, not a human operator.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, LifecycleActors.System, "SYSTEM");

        var client = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == evt.TenantId && c.Id == evt.CustomerEntityId, ct);

        if (client is null)
        {
            // The guard has already committed the inbox row, so the event is
            // acknowledged and will not come back: a missing client here means a stale
            // or bogus reference, not an ordering race (M02 only learns of a client
            // through ClientCreatedEvent, which is published before it can validate
            // anything). Logged loudly so it can be investigated.
            logger.LogWarning(
                "KycValidatedEvent ignored: no client {ClientId} in tenant {TenantId}.",
                evt.CustomerEntityId, evt.TenantId);
            return;
        }

        var statusBefore = client.Status;

        var transition = client.ApplyKycValidated(evt.ValidatedAt, LifecycleActors.System);
        if (transition.IsFailure)
        {
            logger.LogWarning(
                "KycValidatedEvent rejected by client {ClientId} (tenant {TenantId}): {Error}.",
                client.Id, evt.TenantId, transition.Error);
            return;
        }

        // Only a real activation is announced: a Suspended/Archived client keeps its
        // status and must not look activated to downstream modules.
        if (statusBefore != ClientStatus.Active && client.Status == ClientStatus.Active)
        {
            await publisher.PublishAsync(new ClientActivatedEvent(evt.TenantId, client.Id), ct);
        }

        // One SaveChanges commits the status change, the history line and the outbox
        // row together (the inbox row was committed by the guard): either the whole
        // effect of the event is visible, or none of it is.
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "KYC approved for client {ClientId} (tenant {TenantId}): {From} -> {To}.",
            client.Id, evt.TenantId, statusBefore, client.Status);
    }
}
