namespace Sankore.Modules.Notifications;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Internal implementation of INotificationsModule. Callers from other modules
/// only see the INotificationsModule interface — never this class.
/// </summary>
internal sealed class NotificationsModuleFacade(NotificationsDbContext db)
    : INotificationsModule
{
    public async Task<Result<Guid>> QueueEmailAsync(
        QueueEmailRequest request,
        CancellationToken ct = default)
    {
        // Idempotency: silently drop duplicates so callers can retry safely.
        var exists = await db.EmailOutboxMessages
            .IgnoreQueryFilters()
            .AnyAsync(m => m.IdempotencyKey == request.IdempotencyKey, ct);

        if (exists)
            return Result<Guid>.Fail("DUPLICATE_IDEMPOTENCY_KEY");

        var templateDataJson = JsonSerializer.Serialize(request.TemplateData);

        var message = EmailOutboxMessage.Create(
            tenantId: request.TenantId,
            module: request.Module,
            templateKey: request.TemplateKey,
            locale: request.Locale,
            recipientEmail: request.RecipientEmail,
            recipientName: request.RecipientName,
            templateDataJson: templateDataJson,
            idempotencyKey: request.IdempotencyKey);

        db.EmailOutboxMessages.Add(message);
        await db.SaveChangesAsync(ct);

        return Result<Guid>.Ok(message.Id);
    }
}
