namespace Sankore.Modules.Leads.Features.LeadSources.MarkErrorLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class MarkErrorLeadSourceHandler(LeadsDbContext db)
    : IRequestHandler<MarkErrorLeadSourceCommand, Result>
{
    public async Task<Result> Handle(MarkErrorLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        try
        {
            source.MarkError();
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
