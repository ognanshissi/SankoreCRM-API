namespace Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class UpdateOpportunityHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<UpdateOpportunityCommand, Result>
{
    public async Task<Result> Handle(UpdateOpportunityCommand cmd, CancellationToken ct)
    {
        var opportunity = await db.Opportunities
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);

        if (opportunity is null)
            return Result.Fail(OpportunityErrors.NotFound);

        if (opportunity.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        // The amount is a value object: both halves or neither. A currency without an amount would
        // silently keep the old amount under a new currency, which is how a figure changes meaning
        // without anybody editing it.
        var amount = cmd.EstimatedAmount is { } value && !string.IsNullOrWhiteSpace(cmd.EstimatedCurrency)
            ? new Money(value, cmd.EstimatedCurrency)
            : null;

        opportunity.Update(
            title:             cmd.Title,
            description:       cmd.Description,
            estimatedAmount:   amount,
            expectedCloseDate: cmd.ExpectedCloseDate,
            ownerId:           cmd.OwnerId,
            now:               clock.GetUtcNow(),
            product:           cmd.Product,
            nextStep:          cmd.NextStep,
            nextStepDueOn:     cmd.NextStepDueOn,
            confirmSeparateNeed: cmd.ConfirmSeparateNeed);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
