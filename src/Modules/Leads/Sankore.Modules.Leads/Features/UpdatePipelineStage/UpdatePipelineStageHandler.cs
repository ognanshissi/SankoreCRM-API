namespace Sankore.Modules.Leads.Features.UpdatePipelineStage;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdatePipelineStageHandler(LeadsDbContext db)
    : IRequestHandler<UpdatePipelineStageCommand, Result>
{
    public async Task<Result> Handle(UpdatePipelineStageCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        if (lead.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        var result = lead.AdvancePipelineStage(cmd.NewStage);
        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
