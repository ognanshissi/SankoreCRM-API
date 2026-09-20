namespace Sankore.Modules.Leads.Features.ScoringConfigs.CreateScoringConfig;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateScoringConfigHandler(LeadsDbContext db)
    : IRequestHandler<CreateScoringConfigCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateScoringConfigCommand cmd, CancellationToken ct)
    {
        var version = await db.ScoringConfigs
            .Where(c => c.TenantId == cmd.TenantId)
            .CountAsync(ct) + 1;

        var config = ScoringConfig.Create(
            tenantId:               cmd.TenantId,
            version:                version,
            name:                   cmd.Name,
            qualificationThreshold: cmd.QualificationThreshold,
            weightDemographics:     cmd.WeightDemographics,
            weightEngagement:       cmd.WeightEngagement,
            weightProduct:          cmd.WeightProduct,
            weightChannel:          cmd.WeightChannel,
            weightRecency:          cmd.WeightRecency);

        db.ScoringConfigs.Add(config);
        await db.SaveChangesAsync(ct);

        return Result.Ok(config.Id);
    }
}
