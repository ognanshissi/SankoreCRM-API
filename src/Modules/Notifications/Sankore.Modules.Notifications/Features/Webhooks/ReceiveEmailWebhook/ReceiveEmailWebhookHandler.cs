namespace Sankore.Modules.Notifications.Features.Webhooks.ReceiveEmailWebhook;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ReceiveEmailWebhookHandler(
    NotificationsDbContext db,
    IEnumerable<IWebhookParser> parsers,
    ILogger<ReceiveEmailWebhookHandler> logger)
    : IRequestHandler<ReceiveEmailWebhookCommand, Result>
{
    public async Task<Result> Handle(ReceiveEmailWebhookCommand request, CancellationToken ct)
    {
        var parser = parsers.FirstOrDefault(
            p => string.Equals(p.ProviderKey, request.Provider, StringComparison.OrdinalIgnoreCase));

        if (parser is null)
        {
            logger.LogWarning("No webhook parser found for provider '{Provider}'", request.Provider);
            return Result.Ok(); // Still 200 — don't let unknown providers retry forever
        }

        var events = parser.Parse(request.RawBody);

        if (events.Count == 0)
        {
            logger.LogDebug("Webhook parser '{Provider}' returned 0 events — payload ignored or unrecognised",
                request.Provider);
            return Result.Ok();
        }

        foreach (var ev in events)
        {
            db.EmailDeliveryLogs.Add(EmailDeliveryLog.Record(
                request.TenantId,
                outboxMessageId: null, // correlation via external ID not yet implemented
                ev.EventType,
                ev.RecipientEmail,
                request.RawBody.Length > 4000 ? request.RawBody[..4000] : request.RawBody));
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Webhook received | Provider={Provider} Tenant={TenantId} Events={Count}",
            request.Provider, request.TenantId, events.Count);

        return Result.Ok();
    }
}
