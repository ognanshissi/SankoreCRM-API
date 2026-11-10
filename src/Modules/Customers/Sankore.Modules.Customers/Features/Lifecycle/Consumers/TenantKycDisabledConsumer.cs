namespace Sankore.Modules.Customers.Features.Lifecycle.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// The institution switched its KYC off (docs/kyc-par-institution.md, product owner 2026-11-10):
/// the clients waiting for their KYC become customers, each with « KYC désactivé pour
/// l'institution » in its status history — production's and its « Mode test » twin's, which
/// follows the same switch.
///
/// <para>
/// Their KYC files are left as they are (M02 keeps them readable). Switched back on, a project
/// reaching its KYC steps asks for a file again: that is how these clients are taken up, so
/// nothing here is undone on the way back.
/// </para>
/// </summary>
public sealed class TenantKycDisabledConsumer(
    CustomersDbContext db,
    IInboxGuard inbox,
    ITenantKyc tenantKyc,
    ITenantStore tenants,
    TimeProvider clock,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    ILogger<TenantKycDisabledConsumer> logger)
    : IConsumer<TenantKycSwitchedEvent>
{
    public async Task Consume(ConsumeContext<TenantKycSwitchedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        if (evt.Enabled) return;

        if (!await inbox.TryBeginAsync(
                context.MessageId ?? evt.EventId, evt.TenantId, nameof(TenantKycSwitchedEvent), ct))
            return;

        // Switched back on before this ran: the waiting clients keep waiting for their file.
        if (await tenantKyc.IsInForceAsync(evt.TenantId, ct))
            return;

        var twin = (await tenants.GetAsync(evt.TenantId, ct))?.TestTenantId;
        foreach (var tenantId in twin is { } t ? [evt.TenantId, t] : new[] { evt.TenantId })
            await ActivateWaitingAsync(tenantId, ct);
    }

    /// <returns>How many clients became active.</returns>
    internal async Task<int> ActivateWaitingAsync(Guid tenantId, CancellationToken ct)
    {
        using var bg = BackgroundJobContext.SetScope(tenantId, LifecycleActors.System, "SYSTEM");

        var waiting = await db.Clients
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Status == ClientStatus.PendingKyc)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        var activated = 0;
        foreach (var client in waiting)
        {
            if (client.ActivateWithoutKyc(LifecycleActors.System, now, Client.KycDisabledReason).IsFailure)
                continue;
            await publisher.PublishAsync(new ClientActivatedEvent(tenantId, client.Id), ct);
            activated++;
        }

        await db.SaveChangesAsync(ct);

        if (activated > 0)
            logger.LogInformation("KYC switched off for tenant {TenantId}: {Count} waiting clients activated.",
                tenantId, activated);
        return activated;
    }
}
