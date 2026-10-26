namespace Sankore.Modules.Notifications.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>The bell's notifications: read ones after a quarter, any after a year.</summary>
internal sealed class UserNotificationsPurge(NotificationsDbContext db) : ITechnicalPurge
{
    public string Name => "notifications.user_notifications";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var read = now - TechnicalRetention.ReadNotifications;
        var any = now - TechnicalRetention.AnyNotification;
        return db.UserNotifications.IgnoreQueryFilters()
            .Where(n => (n.ReadAt != null && n.ReadAt < read) || n.CreatedAt < any)
            .ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// E-mails and SMS sent — or given up on — and what the providers reported about them. Both hold
/// the recipient and the content. A message still pending or being retried is never touched.
///
/// <para>
/// <b>A client's message keeps a trace</b> (product owner, 2026-10-26): the client record lists the
/// messages a client was sent, for longer than this retention. Past it, such a row loses its
/// recipient, its template data and its last error — what made it personal — and keeps its
/// channel, template, dates and status. The trace goes when the client is anonymised
/// (<c>ClientAnonymizedNotificationsConsumer</c>). A message to nobody in particular is deleted, as
/// before.
/// </para>
/// </summary>
internal sealed class MessageDeliveryPurge(NotificationsDbContext db) : ITechnicalPurge
{
    public string Name => "notifications.email_sms_outbox";

    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.MessageDelivery;

        var count = await db.EmailDeliveryLogs.IgnoreQueryFilters()
            .Where(l => l.RecordedAt < before).ExecuteDeleteAsync(ct);
        count += await db.SmsDeliveryLogs.IgnoreQueryFilters()
            .Where(l => l.RecordedAt < before).ExecuteDeleteAsync(ct);

        var emails = db.EmailOutboxMessages.IgnoreQueryFilters()
            .Where(m => (m.Status == EmailOutboxStatus.Sent || m.Status == EmailOutboxStatus.DeadLettered)
                     && (m.SentAt ?? m.LastAttemptAt ?? m.CreatedAt) < before);
        count += await emails.Where(m => m.ClientId == null).ExecuteDeleteAsync(ct);
        count += await emails.Where(m => m.ClientId != null && m.ContentPurgedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(m => m.RecipientEmail, string.Empty)
                .SetProperty(m => m.RecipientName, (string?)null)
                .SetProperty(m => m.TemplateDataJson, "{}")
                .SetProperty(m => m.LastError, (string?)null)
                .SetProperty(m => m.ContentPurgedAt, now), ct);

        var sms = db.SmsOutboxMessages.IgnoreQueryFilters()
            .Where(m => (m.Status == SmsOutboxStatus.Sent || m.Status == SmsOutboxStatus.DeadLettered)
                     && (m.SentAt ?? m.LastAttemptAt ?? m.CreatedAt) < before);
        count += await sms.Where(m => m.ClientId == null).ExecuteDeleteAsync(ct);
        count += await sms.Where(m => m.ClientId != null && m.ContentPurgedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(m => m.RecipientPhone, string.Empty)
                .SetProperty(m => m.TemplateDataJson, "{}")
                .SetProperty(m => m.LastError, (string?)null)
                .SetProperty(m => m.ContentPurgedAt, now), ct);

        return count;
    }
}
