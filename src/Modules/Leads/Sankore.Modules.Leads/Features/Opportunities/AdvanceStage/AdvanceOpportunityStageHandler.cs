namespace Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class AdvanceOpportunityStageHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<AdvanceOpportunityStageCommand, Result>
{
    public async Task<Result> Handle(AdvanceOpportunityStageCommand cmd, CancellationToken ct)
    {
        var opportunity = await db.Opportunities
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);

        if (opportunity is null)
            return Result.Fail(OpportunityErrors.NotFound);

        if (opportunity.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        var moved = opportunity.AdvanceStage(cmd.NewStage, clock.GetUtcNow());
        if (moved.IsFailure)
            return moved;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
