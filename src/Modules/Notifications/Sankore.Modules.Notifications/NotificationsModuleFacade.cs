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
/// <param name="realtime">
/// Optional for this module's own tests, which build the facade by hand and never notify in-app;
/// the container always supplies one (a no-op where no hub is configured).
/// </param>
internal sealed class NotificationsModuleFacade(
    NotificationsDbContext db,
    Sankore.Shared.Kernel.Realtime.IRealtimeNotifier? realtime = null,
    TimeProvider? clock = null)
    : INotificationsModule
{
    /// <summary>The realtime event a bell listens to. A contract with the browser.</summary>
    public const string InAppCreatedEvent = "notification.created";

    public async Task<IReadOnlyList<ClientMessageSummary>> ListClientMessagesAsync(
        Guid tenantId, Guid clientId, CancellationToken ct = default)
    {
        var emails = await db.EmailOutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.ClientId == clientId)
            .Select(m => new { m.Id, m.TemplateKey, m.Status, m.CreatedAt, m.SentAt, m.RecipientEmail, m.ContentPurgedAt })
            .ToListAsync(ct);

        var sms = await db.SmsOutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.ClientId == clientId)
            .Select(m => new { m.Id, m.TemplateKey, m.Status, m.CreatedAt, m.SentAt, m.RecipientPhone, m.ContentPurgedAt })
            .ToListAsync(ct);

        return
        [
            .. emails.Select(m => new ClientMessageSummary(
                m.Id, "Email", m.TemplateKey, m.Status.ToString(), m.CreatedAt, m.SentAt,
                m.ContentPurgedAt is null ? ClientMessageMasking.Email(m.RecipientEmail) : null)),
            .. sms.Select(m => new ClientMessageSummary(
                m.Id, "Sms", m.TemplateKey, m.Status.ToString(), m.CreatedAt, m.SentAt,
                m.ContentPurgedAt is null ? ClientMessageMasking.Phone(m.RecipientPhone) : null)),
        ];
    }

    public async Task<Result<Guid>> NotifyInAppAsync(InAppNotificationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TenantId == Guid.Empty || request.UserId == Guid.Empty)
            return Result<Guid>.Fail("RECIPIENT_REQUIRED");

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Result<Guid>.Fail("TITLE_AND_KEY_REQUIRED");

        // IgnoreQueryFilters + explicit tenant: the callers are consumers and jobs, where the
        // ambient tenant is whatever SetScope said — this method trusts its argument instead.
        var existing = await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(n => n.TenantId == request.TenantId
                     && n.UserId == request.UserId
                     && n.IdempotencyKey == request.IdempotencyKey)
            .Select(n => (Guid?)n.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is { } id) return Result<Guid>.Ok(id);

        var notification = UserNotification.Create(
            request.TenantId, request.UserId, request.Category, request.Title, request.Body, request.Link,
            request.IdempotencyKey, (clock ?? TimeProvider.System).GetUtcNow());

        db.UserNotifications.Add(notification);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            (ex.InnerException?.Message ?? ex.Message).Contains("ux_user_notifications_idempotency", StringComparison.OrdinalIgnoreCase))
        {
            // A concurrent retry stored it first: one notification, as promised.
            return Result<Guid>.Fail("DUPLICATE_IDEMPOTENCY_KEY");
        }

        // After the commit, and a nudge only — the bell reads the row under the user's own session.
        if (realtime is not null)
        {
            try
            {
                await realtime.PushToUserAsync(
                    request.TenantId, request.UserId, InAppCreatedEvent, new { notificationId = notification.Id }, ct);
            }
            catch (Exception pushError) when (pushError is not OperationCanceledException)
            {
                _ = pushError; // best effort: the bell finds it on its next read.
            }
        }

        return Result<Guid>.Ok(notification.Id);
    }

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
            idempotencyKey: request.IdempotencyKey,
            clientId: request.ClientId);

        db.EmailOutboxMessages.Add(message);
        await db.SaveChangesAsync(ct);

        return Result<Guid>.Ok(message.Id);
    }

    public async Task<Result<Guid>> QueueSmsAsync(
        QueueSmsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A destination is the one thing this module cannot supply or guess. Refused here rather
        // than at send time, so a caller that failed to resolve a telephone number learns it
        // synchronously instead of through a dead-letter row ten seconds later.
        if (string.IsNullOrWhiteSpace(request.RecipientPhone))
            return Result<Guid>.Fail("RECIPIENT_PHONE_REQUIRED");

        // Idempotency: silently drop duplicates so callers can retry safely. On this channel that
        // is also what stops a retried consumer from charging the tenant twice.
        var exists = await db.SmsOutboxMessages
            .IgnoreQueryFilters()
            .AnyAsync(m => m.IdempotencyKey == request.IdempotencyKey, ct);

        if (exists)
            return Result<Guid>.Fail("DUPLICATE_IDEMPOTENCY_KEY");

        var message = SmsOutboxMessage.Create(
            tenantId: request.TenantId,
            module: request.Module,
            templateKey: request.TemplateKey,
            locale: request.Locale,
            recipientPhone: request.RecipientPhone.Trim(),
            templateDataJson: JsonSerializer.Serialize(request.TemplateData),
            idempotencyKey: request.IdempotencyKey,
            clientId: request.ClientId);

        db.SmsOutboxMessages.Add(message);
        await db.SaveChangesAsync(ct);

        return Result<Guid>.Ok(message.Id);
    }
}

/// <summary>
/// How a recipient is shown on the client record: enough to tell two addresses apart, not enough to
/// read one off a screen. An SMS destination already redacted by the processor passes through.
/// </summary>
internal static class ClientMessageMasking
{
    public static string? Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@');
        return at <= 0 ? "•••" : $"{email[0]}•••{email[at..]}";
    }

    public static string? Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        if (phone.Contains('•') || phone.Contains('*')) return phone;
        var digits = phone.Trim();
        return digits.Length <= 4 ? "••••" : $"{digits[..4]}••••{digits[^2..]}";
    }
}
