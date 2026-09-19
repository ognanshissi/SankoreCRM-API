namespace Sankore.Modules.Leads.Features.ReopenLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ReopenLeadHandler(LeadsDbContext db)
    : IRequestHandler<ReopenLeadCommand, Result>
{
    public async Task<Result> Handle(ReopenLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        var result = lead.Reopen();
        if (result.IsFailure)
            return result;

        db.Leads.Update(lead);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
