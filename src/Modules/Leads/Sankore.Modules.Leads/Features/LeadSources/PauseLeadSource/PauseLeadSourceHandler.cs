namespace Sankore.Modules.Leads.Features.LeadSources.PauseLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class PauseLeadSourceHandler(LeadsDbContext db)
    : IRequestHandler<PauseLeadSourceCommand, Result>
{
    public async Task<Result> Handle(PauseLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        source.Pause();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
