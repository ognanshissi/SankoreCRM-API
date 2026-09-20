namespace Sankore.Modules.Leads.Features.PipelineStages.UpdatePipelineStage;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdatePipelineStageConfigHandler(LeadsDbContext db)
    : IRequestHandler<UpdatePipelineStageConfigCommand, Result>
{
    public async Task<Result> Handle(
        UpdatePipelineStageConfigCommand cmd, CancellationToken ct)
    {
        var stage = await db.PipelineStageConfigs
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.StageId, ct);

        if (stage is null)
            return Result.Fail("PIPELINE_STAGE_NOT_FOUND");

        stage.Update(
            label:        cmd.Label,
            description:  cmd.Description,
            displayOrder: cmd.DisplayOrder,
            color:        cmd.Color,
            isFinal:      cmd.IsFinal);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
