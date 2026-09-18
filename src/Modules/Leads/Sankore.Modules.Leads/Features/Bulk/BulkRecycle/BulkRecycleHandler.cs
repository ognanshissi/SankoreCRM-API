namespace Sankore.Modules.Leads.Features.Bulk.BulkRecycle;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class BulkRecycleHandler(LeadsDbContext db)
    : IRequestHandler<BulkRecycleCommand, Result<BulkOperationResult>>
{
    public async Task<Result<BulkOperationResult>> Handle(
        BulkRecycleCommand cmd, CancellationToken ct)
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

            var result = lead.Recycle(cmd.NewSource, cmd.NewCampaign);
            if (result.IsFailure)
                failures.Add(new BulkFailure(id, result.Error!));
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok(new BulkOperationResult(
            Succeeded: ids.Count - failures.Count,
            Failed:    failures.Count,
            Failures:  failures));
    }
}
