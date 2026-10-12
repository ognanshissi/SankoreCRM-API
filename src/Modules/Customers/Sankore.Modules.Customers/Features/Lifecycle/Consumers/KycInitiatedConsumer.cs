using Sankore.Modules.Kyc.PublicApi;

namespace Sankore.Modules.Customers.Features.Lifecycle.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Passe le client en « KYC en cours » à l'ouverture de son dossier.
///
/// Sans lui, <c>Client.KycStatus</c> ne connaissait que trois valeurs — NotStarted, Approved,
/// Rejected — et un client dont le dossier était ouvert depuis des semaines restait affiché
/// « KYC à démarrer ». Le statut client ne change pas : c'est la validation qui active un client,
/// pas l'ouverture du dossier.
///
/// Runs outside any HTTP request: <c>IgnoreQueryFilters()</c> plus an explicit TenantId
/// predicate from the event payload.
/// </summary>
public sealed class KycInitiatedConsumer(
    CustomersDbContext db,
    IInboxGuard inbox,
    ILogger<KycInitiatedConsumer> logger)
    : IConsumer<KycInitiatedEvent>
{
    public async Task Consume(ConsumeContext<KycInitiatedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId, evt.TenantId, nameof(KycInitiatedEvent), ct))
            return;

        using var bg = BackgroundJobContext.SetScope(evt.TenantId, LifecycleActors.System, "SYSTEM");

        var client = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == evt.TenantId && c.Id == evt.CustomerEntityId, ct);

        if (client is null)
        {
            logger.LogWarning(
                "KycInitiatedEvent ignored: no client {ClientId} in tenant {TenantId}.",
                evt.CustomerEntityId, evt.TenantId);
            return;
        }

        if (client.ApplyKycInitiated(evt.OccurredAt))
            await db.SaveChangesAsync(ct);
    }
}
