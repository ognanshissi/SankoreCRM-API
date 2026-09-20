namespace Sankore.Modules.Leads.Features.ScoringConfigs.ActivateScoringConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateScoringConfigHandler(LeadsDbContext db)
    : IRequestHandler<ActivateScoringConfigCommand, Result>
{
    public async Task<Result> Handle(
        ActivateScoringConfigCommand cmd, CancellationToken ct)
    {
        var config = await db.ScoringConfigs
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConfigId, ct);

        if (config is null)
            return Result.Fail("SCORING_CONFIG_NOT_FOUND");

        // Deactivate the currently active config for this tenant (if any)
        var currentActive = await db.ScoringConfigs
            .AsTracking()
            .Where(c => c.TenantId == config.TenantId && c.IsActive && c.Id != config.Id)
            .ToListAsync(ct);

        foreach (var active in currentActive)
            active.Deactivate();

        config.Activate();
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
