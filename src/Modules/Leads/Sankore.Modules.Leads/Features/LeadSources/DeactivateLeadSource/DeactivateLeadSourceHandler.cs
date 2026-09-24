namespace Sankore.Modules.Leads.Features.LeadSources.DeactivateLeadSource;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateLeadSourceHandler(
    LeadsDbContext db,
    ISecretsModule secrets)
    : IRequestHandler<DeactivateLeadSourceCommand, Result>
{
    public async Task<Result> Handle(DeactivateLeadSourceCommand cmd, CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SourceId, ct);

        if (source is null)
            return Result.Fail("LEAD_SOURCE_NOT_FOUND");

        try
        {
            source.Archive();
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        await db.SaveChangesAsync(ct);

        // Purge all vault secrets for this source
        await secrets.DeleteAllAsync(source.TenantId, "LeadSource", source.Id, ct);

        return Result.Ok();
    }
}
