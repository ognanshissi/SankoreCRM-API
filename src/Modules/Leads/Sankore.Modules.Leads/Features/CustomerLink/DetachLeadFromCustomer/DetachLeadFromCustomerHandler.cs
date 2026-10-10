namespace Sankore.Modules.Leads.Features.CustomerLink.DetachLeadFromCustomer;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CustomerLink.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class DetachLeadFromCustomerHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<DetachLeadFromCustomerCommand, Result>
{
    public async Task<Result> Handle(DetachLeadFromCustomerCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);
        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        if (lead.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        // Captured before the detach clears it.
        var previous = lead.CustomerId;

        var detach = lead.DetachFromCustomer(cmd.Reason);
        if (detach.IsFailure)
            return detach;

        await publisher.PublishAsync(
            new LeadDetachedFromCustomerIntegrationEvent(
                TenantId:           lead.TenantId,
                LeadId:             lead.Id,
                PreviousCustomerId: previous!.Value),
            ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
