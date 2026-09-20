namespace Sankore.Modules.Leads.Features.SlaConfigs.DeactivateSlaConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateSlaConfigHandler(LeadsDbContext db)
    : IRequestHandler<DeactivateSlaConfigCommand, Result>
{
    public async Task<Result> Handle(
        DeactivateSlaConfigCommand cmd, CancellationToken ct)
    {
        var config = await db.SlaConfigs
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SlaConfigId, ct);

        if (config is null)
            return Result.Fail("SLA_CONFIG_NOT_FOUND");

        config.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
