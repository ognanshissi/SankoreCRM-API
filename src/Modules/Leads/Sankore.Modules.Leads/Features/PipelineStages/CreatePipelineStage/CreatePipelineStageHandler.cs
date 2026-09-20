namespace Sankore.Modules.Leads.Features.PipelineStages.CreatePipelineStage;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreatePipelineStageHandler(LeadsDbContext db)
    : IRequestHandler<CreatePipelineStageCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreatePipelineStageCommand cmd, CancellationToken ct)
    {
        var normalizedCode = cmd.Code.Trim().ToUpperInvariant();

        var codeExists = await db.PipelineStageConfigs
            .AnyAsync(s => s.Code == normalizedCode, ct);

        if (codeExists)
            return Result.Fail<Guid>("PIPELINE_STAGE_CODE_ALREADY_EXISTS");

        var stage = PipelineStageConfig.Create(
            tenantId:     cmd.TenantId,
            code:         cmd.Code,
            label:        cmd.Label,
            displayOrder: cmd.DisplayOrder,
            description:  cmd.Description,
            color:        cmd.Color,
            isFinal:      cmd.IsFinal);

        db.PipelineStageConfigs.Add(stage);
        await db.SaveChangesAsync(ct);

        return Result.Ok(stage.Id);
    }
}
