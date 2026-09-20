namespace Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateLeadSourceHandler(LeadsDbContext db)
    : IRequestHandler<UpdateLeadSourceCommand, Result>
{
    public async Task<Result> Handle(UpdateLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        var codeConflict = await db.LeadSourceConfigs
            .AnyAsync(s => s.Code == cmd.Code && s.Id != cmd.SourceId, ct);

        if (codeConflict)
            return Result.Fail("LEAD_SOURCE_CODE_ALREADY_EXISTS");

        source.Update(cmd.Code, cmd.Label, cmd.DisplayOrder, cmd.Description);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
