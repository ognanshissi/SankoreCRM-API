namespace Sankore.Modules.Notifications.Infrastructure.Retention;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>The messages and notifications of a TEST institution, emptied (data-purge lot 4); its templates stay.</summary>
internal sealed class NotificationsTenantDataWipe(NotificationsDbContext db, ILogger<NotificationsTenantDataWipe> logger) : ITenantDataWipe
{
    internal static readonly Type[] Keep = [typeof(EmailTemplate), typeof(SmsTemplate)];

    public string Module => "Notifications";

    public Task<int> WipeAsync(Guid tenantId, CancellationToken ct) => TenantRows.WipeAsync(db, tenantId, Keep, logger, ct);
}
