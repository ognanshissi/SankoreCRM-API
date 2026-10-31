namespace Sankore.Modules.Leads.Features.Bulk.BulkClose;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class BulkCloseHandler(
    LeadsDbContext db,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher? publisher = null,
    AgentCapacityService? capacity = null)
    : IRequestHandler<BulkCloseCommand, Result<BulkOperationResult>>
{
    public async Task<Result<BulkOperationResult>> Handle(
        BulkCloseCommand cmd, CancellationToken ct)
    {
        var ids = cmd.LeadIds.Distinct().ToList();

        var leads = await db.Leads
            .AsTracking()
            .Where(l => ids.Contains(l.Id))
            .ToListAsync(ct);

        var failures = new List<BulkFailure>();

        foreach (var id in ids)
        {
            var lead = leads.FirstOrDefault(l => l.Id == id);
            if (lead is null)
            {
                failures.Add(new BulkFailure(id, "LEAD_NOT_FOUND"));
                continue;
            }

            var result = lead.Close(cmd.Reason, cmd.Detail);
            if (result.IsFailure)
                failures.Add(new BulkFailure(id, result.Error!));
        }

        // The archived ones only: a lead refused above keeps its tasks.
        var archived = cmd.Reason == LeadCloseReason.Archived
            ? ids.Where(id => failures.All(f => f.LeadId != id) && leads.Any(l => l.Id == id)).ToList()
            : [];
        var agents = await ArchivedLeadTasks.CancelOpenAsync(db, publisher, archived, ct);

        await db.SaveChangesAsync(ct);

        if (capacity is not null)
            foreach (var (tenantId, agentId) in agents)
                await capacity.InvalidateAsync(tenantId, agentId, ct);

        return Result.Ok(new BulkOperationResult(
            Succeeded: ids.Count - failures.Count,
            Failed:    failures.Count,
            Failures:  failures));
    }
}
