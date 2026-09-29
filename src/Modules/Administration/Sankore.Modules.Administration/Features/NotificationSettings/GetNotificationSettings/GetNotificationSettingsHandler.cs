using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.GetNotificationSettings;

internal sealed class GetNotificationSettingsHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser)
    : IRequestHandler<GetNotificationSettingsQuery, Result<NotificationSettingsDto>>
{
    public async Task<Result<NotificationSettingsDto>> Handle(
        GetNotificationSettingsQuery request, CancellationToken ct)
    {
        var settings = await db.TenantNotificationSettings
            .FirstOrDefaultAsync(ct);

        // Lazy-init: return platform defaults when no row exists yet
        if (settings is null)
        {
            return Result.Ok(new NotificationSettingsDto(
                ProviderType: "Smtp",
                UseDefaultPlatformProvider: true,
                FromEmail: null,
                FromName: null,
                ReplyToEmail: null,
                SendingDomain: null,
                HasCredential: false,
                SmtpHost: null,
                SmtpPort: null,
                SmtpUsername: null,
                SmtpUseSsl: false,
                SmtpUseStartTls: false,
                MonthlyQuotaLimit: null,
                CurrentMonthUsageCount: 0,
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        return Result.Ok(new NotificationSettingsDto(
            settings.ProviderType,
            settings.UseDefaultPlatformProvider,
            settings.FromEmail,
            settings.FromName,
            settings.ReplyToEmail,
            settings.SendingDomain,
            settings.HasCredential,
            settings.SmtpHost,
            settings.SmtpPort,
            settings.SmtpUsername,
            settings.SmtpUseSsl,
            settings.SmtpUseStartTls,
            settings.MonthlyQuotaLimit,
            settings.CurrentMonthUsageCount,
            settings.UpdatedAt));
    }
}
