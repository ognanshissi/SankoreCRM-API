namespace Sankore.Modules.Leads.Features.SlaConfigs.ActivateSlaConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateSlaConfigHandler(LeadsDbContext db)
    : IRequestHandler<ActivateSlaConfigCommand, Result>
{
    public async Task<Result> Handle(
        ActivateSlaConfigCommand cmd, CancellationToken ct)
    {
        var config = await db.SlaConfigs
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.SlaConfigId, ct);

        if (config is null)
            return Result.Fail("SLA_CONFIG_NOT_FOUND");

        // Ensure at most one active SLA per (TenantId, AgencyId) — deactivate any existing
        var existing = await db.SlaConfigs
            .AsTracking()
            .Where(s => s.TenantId == config.TenantId
                     && s.AgencyId == config.AgencyId
                     && s.IsActive
                     && s.Id != config.Id)
            .ToListAsync(ct);

        foreach (var e in existing)
            e.Deactivate();

        config.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
