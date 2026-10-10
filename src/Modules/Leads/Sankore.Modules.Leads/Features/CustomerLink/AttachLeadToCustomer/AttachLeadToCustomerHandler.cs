namespace Sankore.Modules.Leads.Features.CustomerLink.AttachLeadToCustomer;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CustomerLink.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class AttachLeadToCustomerHandler(
    LeadsDbContext db,
    ICustomersModule customers,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<AttachLeadToCustomerCommand, Result>
{
    public async Task<Result> Handle(AttachLeadToCustomerCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);
        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        if (lead.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        // Resolved, not just checked for existence: a client id an agent is holding may have been
        // merged away since they loaded the screen, and attaching to the absorbed record would
        // point the whole request at an id M01 no longer answers for.
        var surviving = await customers.ResolveClientIdAsync(lead.TenantId, cmd.CustomerId, ct);
        if (surviving is null)
            return Result.Fail("CUSTOMER_NOT_FOUND");

        var attach = lead.AttachToCustomer(surviving.Value, isAutomatic: false, cmd.Reason);
        if (attach.IsFailure)
            return attach;

        // Ids only, through the outbox, in the lead's own transaction. The client's identity,
        // household, PEP and FATCA stay on the client record (BNF-B-23) — a consumer that needs
        // them reads M01.
        await publisher.PublishAsync(
            new LeadLinkedToCustomerIntegrationEvent(
                TenantId:    lead.TenantId,
                LeadId:      lead.Id,
                CustomerId:  surviving.Value,
                IsAutomatic: false),
            ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
