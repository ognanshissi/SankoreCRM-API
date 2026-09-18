namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Features.ConvertLead.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class ConvertLeadHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<ConvertLeadCommand, Result<ConvertLeadResult>>
{
    public async Task<Result<ConvertLeadResult>> Handle(
        ConvertLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<ConvertLeadResult>("LEAD_NOT_FOUND");

        // If the caller didn't supply a CustomerId we generate one here.
        // The Customers module will use this same Guid when it creates the
        // customer record in response to the LeadConvertedIntegrationEvent.
        var customerId = cmd.CustomerId ?? Guid.NewGuid();

        var convertResult = lead.Convert(customerId);
        if (convertResult.IsFailure)
            return Result.Fail<ConvertLeadResult>(convertResult.Error!);

        // Publish the integration event atomically with the lead state change.
        // OutboxEventPublisher writes the row into THIS DbContext instance
        // (no SaveChangesAsync inside), so both rows land in the same commit.
        await publisher.PublishAsync(
            new LeadConvertedIntegrationEvent(
                LeadId:      lead.Id,
                CustomerId:  customerId,
                TenantId:    lead.TenantId,
                FullName:    lead.FullName,
                PhoneNumber: lead.PhoneNumber,
                Email:       lead.Email,
                ConvertedAt: lead.ConvertedAt!.Value),
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new ConvertLeadResult(
            LeadId:      lead.Id,
            CustomerId:  customerId,
            ConvertedAt: lead.ConvertedAt!.Value));
    }
}
