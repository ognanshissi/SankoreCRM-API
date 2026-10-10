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

        // No db.Leads.Update(lead): the lead is already tracked, and calling Update marks the
        // OWNED PrincipalPep's identifying key as modified — EF then refuses the save with
        // "part of a key and so cannot be modified".
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
