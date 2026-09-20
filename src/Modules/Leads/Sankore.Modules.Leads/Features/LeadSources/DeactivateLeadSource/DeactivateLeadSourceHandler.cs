namespace Sankore.Modules.Leads.Features.LeadSources.DeactivateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateLeadSourceHandler(LeadsDbContext db)
    : IRequestHandler<DeactivateLeadSourceCommand, Result>
{
    public async Task<Result> Handle(DeactivateLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        if (source.IsSystem)
            return Result.Fail("CANNOT_DEACTIVATE_SYSTEM_SOURCE");

        source.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
