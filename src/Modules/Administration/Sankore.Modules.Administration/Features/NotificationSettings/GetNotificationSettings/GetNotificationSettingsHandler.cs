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
                ProviderType: "Default",
                UseDefaultPlatformProvider: true,
                FromEmail: null,
                FromName: null,
                ReplyToEmail: null,
                SendingDomain: null,
                CredentialVaultPathRef: null,
                MonthlyQuotaLimit: null,
                CurrentMonthUsageCount: 0,
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        // Mask vault path: expose only the reference path, never the secret value
        var maskedVaultRef = settings.CredentialVaultPath is not null
            ? $"vault://{settings.CredentialVaultPath}"
            : null;

        return Result.Ok(new NotificationSettingsDto(
            settings.ProviderType,
            settings.UseDefaultPlatformProvider,
            settings.FromEmail,
            settings.FromName,
            settings.ReplyToEmail,
            settings.SendingDomain,
            maskedVaultRef,
            settings.MonthlyQuotaLimit,
            settings.CurrentMonthUsageCount,
            settings.UpdatedAt));
    }
}
