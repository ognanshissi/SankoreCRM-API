namespace Sankore.Modules.Leads.Features.PipelineStages.ActivatePipelineStage;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivatePipelineStageHandler(LeadsDbContext db)
    : IRequestHandler<ActivatePipelineStageCommand, Result>
{
    public async Task<Result> Handle(
        ActivatePipelineStageCommand cmd, CancellationToken ct)
    {
        var stage = await db.PipelineStageConfigs
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.StageId, ct);

        if (stage is null)
            return Result.Fail("PIPELINE_STAGE_NOT_FOUND");

        stage.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
