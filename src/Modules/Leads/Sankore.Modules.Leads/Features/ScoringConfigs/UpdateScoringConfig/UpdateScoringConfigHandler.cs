namespace Sankore.Modules.Leads.Features.ScoringConfigs.UpdateScoringConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateScoringConfigHandler(LeadsDbContext db)
    : IRequestHandler<UpdateScoringConfigCommand, Result>
{
    public async Task<Result> Handle(
        UpdateScoringConfigCommand cmd, CancellationToken ct)
    {
        var config = await db.ScoringConfigs
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ConfigId, ct);

        if (config is null)
            return Result.Fail("SCORING_CONFIG_NOT_FOUND");

        if (config.IsActive)
            return Result.Fail("CANNOT_UPDATE_ACTIVE_CONFIG");

        config.Update(
            name:                   cmd.Name,
            qualificationThreshold: cmd.QualificationThreshold,
            weightDemographics:     cmd.WeightDemographics,
            weightEngagement:       cmd.WeightEngagement,
            weightProduct:          cmd.WeightProduct,
            weightChannel:          cmd.WeightChannel,
            weightRecency:          cmd.WeightRecency);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
