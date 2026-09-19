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

        var result = lead.ReturnToQueue();
        if (result.IsFailure)
            return result;

        db.Leads.Update(lead);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
