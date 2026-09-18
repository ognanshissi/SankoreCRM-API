namespace Sankore.Modules.Leads.Features.SetQualificationCompleteness;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SetQualificationCompletenessHandler(LeadsDbContext db)
    : IRequestHandler<SetQualificationCompletenessCommand, Result>
{
    public async Task<Result> Handle(SetQualificationCompletenessCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        lead.SetQualificationCompleteness(cmd.Completeness);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
