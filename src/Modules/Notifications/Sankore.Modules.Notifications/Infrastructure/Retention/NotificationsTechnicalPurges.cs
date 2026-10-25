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

        count += await db.EmailOutboxMessages.IgnoreQueryFilters()
            .Where(m => (m.Status == EmailOutboxStatus.Sent || m.Status == EmailOutboxStatus.DeadLettered)
                     && (m.SentAt ?? m.LastAttemptAt ?? m.CreatedAt) < before)
            .ExecuteDeleteAsync(ct);
        count += await db.SmsOutboxMessages.IgnoreQueryFilters()
            .Where(m => (m.Status == SmsOutboxStatus.Sent || m.Status == SmsOutboxStatus.DeadLettered)
                     && (m.SentAt ?? m.LastAttemptAt ?? m.CreatedAt) < before)
            .ExecuteDeleteAsync(ct);
        return count;
    }
}
