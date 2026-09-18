namespace Sankore.Modules.Leads.Features.UpdateLeadOwner;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateLeadOwnerHandler(LeadsDbContext db)
    : IRequestHandler<UpdateLeadOwnerCommand, Result>
{
    public async Task<Result> Handle(UpdateLeadOwnerCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        var result = lead.SetOwner(cmd.OwnerId);
        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
