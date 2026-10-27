namespace Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The legacy « by phase » move (the client's Projets tab). It used to set the phase and clear the
/// step, which skipped every check of the template — the KYC markers, the step conditions, the
/// validation circuits. A phase that has a step now moves onto its FIRST step through the same
/// transition as the step endpoint; only a phase the template has no step for keeps the old move.
/// </summary>
internal sealed class AdvanceOpportunityStageHandler(
    LeadsDbContext db,
    TimeProvider clock,
    ProjectTemplates.ProjectTemplateResolver? templates = null,
    ProjectTemplates.ProjectStepTransition? transition = null)
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

        if (templates is not null && transition is not null)
        {
            var template = await templates.ForProjectAsync(opportunity, ct);
            var step = template.Steps.FirstOrDefault(s => s.Phase == cmd.NewStage);
            if (step is not null)
            {
                var stepped = await transition.MoveAsync(opportunity, template, step, ct);
                if (stepped.IsFailure)
                    return stepped;

                await db.SaveChangesAsync(ct);
                return Result.Ok();
            }
        }

        var moved = opportunity.AdvanceStage(cmd.NewStage, clock.GetUtcNow());
        if (moved.IsFailure)
            return moved;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
