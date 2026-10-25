namespace Sankore.Modules.Leads.Features.Retention;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>Erases one lead now — see <c>ILeadsModule.EraseLeadAsync</c>.</summary>
internal sealed record EraseLeadCommand(Guid TenantId, Guid LeadId) : IRequest<bool>;

internal sealed class EraseLeadHandler(
    LeadsDbContext db,
    TimeProvider clock,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<EraseLeadCommand, bool>
{
    public async Task<bool> Handle(EraseLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.TenantId == cmd.TenantId && l.Id == cmd.LeadId, ct);

        // Idempotent: a retried approval must not publish a second event.
        if (lead is null || lead.IsAnonymized)
            return false;

        var now = clock.GetUtcNow();
        await LeadErasure.AnonymizeAsync(db, cmd.TenantId, [lead], now, ct);

        // Same transaction as the erasure: no consumer sees an event for data still there.
        await publisher.PublishAsync(new LeadErasedEvent(cmd.TenantId, lead.Id, now), ct);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
