namespace Sankore.Modules.Notifications.Features.ClientMessages;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Notifications.Infrastructure;

/// <summary>
/// The e-mails and SMS a client was sent, removed with the client (retention, or a person's
/// request). Their trace outlives the technical retention for the client record's sake
/// (2026-10-26); once there is no client record, nothing justifies keeping it.
/// </summary>
internal static class ClientMessageErasure
{
    public static async Task<int> EraseAsync(NotificationsDbContext db, Guid tenantId, Guid clientId, CancellationToken ct)
        => await db.EmailOutboxMessages.IgnoreQueryFilters()
               .Where(m => m.TenantId == tenantId && m.ClientId == clientId).ExecuteDeleteAsync(ct)
         + await db.SmsOutboxMessages.IgnoreQueryFilters()
               .Where(m => m.TenantId == tenantId && m.ClientId == clientId).ExecuteDeleteAsync(ct);
}

/// <summary>Idempotent: a second delivery finds nothing left to delete.</summary>
public sealed class ClientAnonymizedNotificationsConsumer(
    NotificationsDbContext db, ILogger<ClientAnonymizedNotificationsConsumer> logger)
    : IConsumer<ClientAnonymizedEvent>
{
    public async Task Consume(ConsumeContext<ClientAnonymizedEvent> context)
    {
        var evt = context.Message;
        if (evt.TenantId == Guid.Empty || evt.ClientId == Guid.Empty) return;

        var removed = await ClientMessageErasure.EraseAsync(db, evt.TenantId, evt.ClientId, context.CancellationToken);
        logger.LogInformation(
            "Client {ClientId} anonymised: {Count} message(s) it was sent removed", evt.ClientId, removed);
    }
}
