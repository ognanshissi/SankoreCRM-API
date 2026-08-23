using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.UpdateNotificationSettings;

internal sealed class UpdateNotificationSettingsHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    IDistributedCache cache)
    : IRequestHandler<UpdateNotificationSettingsCommand, Result>
{
    public async Task<Result> Handle(
        UpdateNotificationSettingsCommand request, CancellationToken ct)
    {
        var settings = await db.TenantNotificationSettings
            .FirstOrDefaultAsync(ct);

        if (settings is null)
        {
            settings = TenantNotificationSettings.CreateDefault(
                currentUser.TenantId, currentUser.Id);
            db.TenantNotificationSettings.Add(settings);
        }

        settings.UpdateProvider(
            request.ProviderType,
            request.FromEmail,
            request.FromName,
            request.ReplyToEmail,
            request.SendingDomain,
            request.CredentialVaultPath,
            currentUser.Id);

        // Publish outbox event atomically with the settings change
        await publisher.PublishAsync(
            new TenantNotificationSettingsChangedEvent(currentUser.TenantId), ct);

        await db.SaveChangesAsync(ct);

        // Immediately invalidate the provider-resolution cache (best-effort)
        await cache.RemoveAsync($"notifications:provider:{currentUser.TenantId}", ct);

        return Result.Ok();
    }
}
