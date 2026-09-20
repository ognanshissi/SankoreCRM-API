namespace Sankore.Modules.Leads.Features.PipelineStages.DeactivatePipelineStage;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivatePipelineStageHandler(LeadsDbContext db)
    : IRequestHandler<DeactivatePipelineStageCommand, Result>
{
    public async Task<Result> Handle(
        DeactivatePipelineStageCommand cmd, CancellationToken ct)
    {
        var stage = await db.PipelineStageConfigs
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.StageId, ct);

        if (stage is null)
            return Result.Fail("PIPELINE_STAGE_NOT_FOUND");

        var result = stage.Deactivate();
        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
