namespace Sankore.Modules.Leads.Features.UpdateLeadOwner;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.UpdateLeadOwner.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class UpdateLeadOwnerHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<UpdateLeadOwnerCommand, Result>
{
    public async Task<Result> Handle(UpdateLeadOwnerCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        var previousOwnerId = lead.OwnerId;

        var result = lead.SetOwner(cmd.OwnerId);
        if (result.IsFailure)
            return result;

        var history = LeadOwnerAssignmentHistory.Create(
            tenantId:         lead.TenantId,
            leadId:           lead.Id,
            previousOwnerId:  previousOwnerId,
            newOwnerId:       cmd.OwnerId,
            assignmentMethod: cmd.AssignmentMethod,
            assignedBy:       cmd.AssignedBy,
            reason:           cmd.Reason);

        db.LeadOwnerAssignmentHistories.Add(history);

        await publisher.PublishAsync(
            new LeadOwnerChangedIntegrationEvent(
                LeadId:           lead.Id,
                TenantId:         lead.TenantId,
                PreviousOwnerId:  previousOwnerId,
                NewOwnerId:       cmd.OwnerId,
                AssignmentMethod: cmd.AssignmentMethod,
                Reason:           cmd.Reason,
                AssignedBy:       cmd.AssignedBy,
                AssignedAt:       history.AssignedAt),
            ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
