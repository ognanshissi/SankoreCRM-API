namespace Sankore.Modules.Leads.Features.ReturnLeadToQueue;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ReturnLeadToQueueHandler(LeadsDbContext db)
    : IRequestHandler<ReturnLeadToQueueCommand, Result>
{
    public async Task<Result> Handle(ReturnLeadToQueueCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        // The assignment being given up: the aggregate stamps it as superseded, and without the
        // row it refuses rather than leaving it open to keep breaching its SLA.
        var current = lead.CurrentAssignmentId is { } currentId
            ? await db.LeadAssignments.AsTracking().FirstOrDefaultAsync(a => a.Id == currentId, ct)
            : null;

        var result = lead.ReturnToQueue(current);
        if (result.IsFailure)
            return result;

        db.Leads.Update(lead);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
