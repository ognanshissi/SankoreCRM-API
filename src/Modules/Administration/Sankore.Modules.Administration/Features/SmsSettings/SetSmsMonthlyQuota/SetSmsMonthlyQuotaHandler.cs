namespace Sankore.Modules.Administration.Features.SmsSettings.SetSmsMonthlyQuota;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.SmsSettings.UpdateSmsSettings;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class SetSmsMonthlyQuotaHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(AdministrationDbContext))] IEventPublisher publisher,
    IDistributedCache cache)
    : IRequestHandler<SetSmsMonthlyQuotaCommand, Result>
{
    public async Task<Result> Handle(SetSmsMonthlyQuotaCommand request, CancellationToken ct)
    {
        // AsTracking: NoTracking by default would mutate a detached row and write nothing.
        var settings = await db.TenantSmsSettings.AsTracking().FirstOrDefaultAsync(ct);

        if (settings is null)
        {
            // A ceiling set before a provider is chosen is legitimate — a platform administrator
            // capping a new tenant before it configures anything — so the row is created
            // Disabled and the cap waits on it.
            settings = TenantSmsSettings.CreateDefault(currentUser.TenantId, currentUser.Id);
            db.TenantSmsSettings.Add(settings);
        }

        settings.SetMonthlyQuota(request.MonthlyQuotaLimit, currentUser.Id);

        await publisher.PublishAsync(new TenantSmsSettingsChangedEvent(currentUser.TenantId), ct);
        await db.SaveChangesAsync(ct);

        await cache.RemoveAsync(UpdateSmsSettingsHandler.CacheKey(currentUser.TenantId), ct);

        return Result.Ok();
    }
}
