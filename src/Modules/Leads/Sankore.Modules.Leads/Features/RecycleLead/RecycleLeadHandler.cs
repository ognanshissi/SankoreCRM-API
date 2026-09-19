namespace Sankore.Modules.Leads.Features.RecycleLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RecycleLeadHandler(LeadsDbContext db)
    : IRequestHandler<RecycleLeadCommand, Result>
{
    public async Task<Result> Handle(RecycleLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        var result = lead.Recycle(cmd.NewSource, cmd.NewCampaign);
        if (result.IsFailure)
            return result;
        
        db.Leads.Update(lead);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
