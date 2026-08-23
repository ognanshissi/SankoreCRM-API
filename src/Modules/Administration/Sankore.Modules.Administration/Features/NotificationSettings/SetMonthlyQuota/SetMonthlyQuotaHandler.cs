using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.NotificationSettings.SetMonthlyQuota;

internal sealed class SetMonthlyQuotaHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    IDistributedCache cache)
    : IRequestHandler<SetMonthlyQuotaCommand, Result>
{
    public async Task<Result> Handle(SetMonthlyQuotaCommand request, CancellationToken ct)
    {
        var settings = await db.TenantNotificationSettings
            .FirstOrDefaultAsync(ct);

        if (settings is null)
        {
            settings = TenantNotificationSettings.CreateDefault(
                currentUser.TenantId, currentUser.Id);
            db.TenantNotificationSettings.Add(settings);
        }

        settings.SetMonthlyQuota(request.MonthlyQuotaLimit, currentUser.Id);

        await publisher.PublishAsync(
            new TenantNotificationSettingsChangedEvent(currentUser.TenantId), ct);

        await db.SaveChangesAsync(ct);

        await cache.RemoveAsync($"notifications:provider:{currentUser.TenantId}", ct);

        return Result.Ok();
    }
}
