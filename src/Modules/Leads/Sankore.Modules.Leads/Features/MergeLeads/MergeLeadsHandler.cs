namespace Sankore.Modules.Leads.Features.MergeLeads;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class MergeLeadsHandler(LeadsDbContext db)
    : IRequestHandler<MergeLeadsCommand, Result>
{
    public async Task<Result> Handle(MergeLeadsCommand cmd, CancellationToken ct)
    {
        var target = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.TargetLeadId, ct);

        if (target is null)
            return Result.Fail("TARGET_LEAD_NOT_FOUND");

        var source = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.SourceLeadId, ct);

        if (source is null)
            return Result.Fail("SOURCE_LEAD_NOT_FOUND");

        if (source.Status == LeadStatus.Archived)
            return Result.Fail("SOURCE_LEAD_IS_ALREADY_ARCHIVED");

        if (target.Status == LeadStatus.Archived)
            return Result.Fail("TARGET_LEAD_IS_ARCHIVED");

        // Transfer activities from source → target (no uniqueness constraint).
        await db.LeadActivities
            .Where(a => a.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LeadId, cmd.TargetLeadId), ct);

        // Transfer reminders from source → target.
        await db.LeadReminders
            .Where(r => r.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeadId, cmd.TargetLeadId), ct);

        // Transfer tags: drop source tags already present on target (unique index guard),
        // then reassign the remainder.
        var existingTargetTags = await db.LeadTags
            .Where(t => t.LeadId == cmd.TargetLeadId)
            .Select(t => t.Tag)
            .ToListAsync(ct);

        if (existingTargetTags.Count > 0)
        {
            await db.LeadTags
                .Where(t => t.LeadId == cmd.SourceLeadId && existingTargetTags.Contains(t.Tag))
                .ExecuteDeleteAsync(ct);
        }

        await db.LeadTags
            .Where(t => t.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LeadId, cmd.TargetLeadId), ct);

        // Archive the source lead.
        var closeResult = source.Close(
            LeadCloseReason.Archived,
            $"Merged into lead {cmd.TargetLeadId} by {cmd.MergedBy}");

        if (closeResult.IsFailure)
            return closeResult;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
