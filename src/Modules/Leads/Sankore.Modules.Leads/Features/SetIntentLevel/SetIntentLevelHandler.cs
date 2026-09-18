namespace Sankore.Modules.Leads.Features.SetIntentLevel;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SetIntentLevelHandler(LeadsDbContext db)
    : IRequestHandler<SetIntentLevelCommand, Result>
{
    public async Task<Result> Handle(SetIntentLevelCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        lead.UpdateIntentLevel(cmd.IntentLevel);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
