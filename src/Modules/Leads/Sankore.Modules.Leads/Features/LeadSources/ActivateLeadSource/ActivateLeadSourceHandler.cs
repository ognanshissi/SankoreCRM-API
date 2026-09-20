namespace Sankore.Modules.Leads.Features.LeadSources.ActivateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateLeadSourceHandler(LeadsDbContext db)
    : IRequestHandler<ActivateLeadSourceCommand, Result>
{
    public async Task<Result> Handle(ActivateLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        source.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
