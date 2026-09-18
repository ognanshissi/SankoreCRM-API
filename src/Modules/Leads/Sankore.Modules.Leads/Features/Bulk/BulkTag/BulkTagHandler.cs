namespace Sankore.Modules.Leads.Features.Bulk.BulkTag;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class BulkTagHandler(LeadsDbContext db)
    : IRequestHandler<BulkTagCommand, Result<BulkOperationResult>>
{
    public async Task<Result<BulkOperationResult>> Handle(
        BulkTagCommand cmd, CancellationToken ct)
    {
        var ids = cmd.LeadIds.Distinct().ToList();
        var normalised = cmd.Tag.Trim().ToLowerInvariant();

        var leads = await db.Leads
            .Where(l => ids.Contains(l.Id))
            .ToListAsync(ct);

        // Load existing tags for these leads to support idempotency.
        var existingTags = await db.LeadTags
            .Where(t => ids.Contains(t.LeadId) && t.Tag == normalised)
            .Select(t => t.LeadId)
            .ToHashSetAsync(ct);

        var failures = new List<BulkFailure>();

        foreach (var id in ids)
        {
            var lead = leads.FirstOrDefault(l => l.Id == id);
            if (lead is null)
            {
                failures.Add(new BulkFailure(id, "LEAD_NOT_FOUND"));
                continue;
            }

            if (existingTags.Contains(id))
                continue; // Already tagged — treat as success.

            db.LeadTags.Add(LeadTag.Create(lead.TenantId, id, normalised, cmd.AddedBy));
        }

        await db.SaveChangesAsync(ct);

        return Result.Ok(new BulkOperationResult(
            Succeeded: ids.Count - failures.Count,
            Failed:    failures.Count,
            Failures:  failures));
    }
}
