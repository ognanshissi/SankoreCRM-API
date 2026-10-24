namespace Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class UpdateOpportunityHandler(
    LeadsDbContext db,
    TimeProvider clock,
    Administration.PublicApi.IAdministrationModule? administration = null,
    ProjectTemplates.ProjectTemplateResolver? templates = null)
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

        var previousProduct = opportunity.Product;

        // Only when the product changes: a patch cannot clear the date, and a project created
        // before its template made the date critical must stay editable — its screen asks for one.
        if (cmd.Product is not null && cmd.Product != previousProduct
            && await ProjectTemplates.ProjectCloseDateRule.MissingAsync(
                templates, cmd.Product, cmd.ExpectedCloseDate ?? opportunity.ExpectedCloseDate, ct))
            return Result.Fail(ProjectTemplateErrors.CloseDateRequired);

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

        // The line follows the product: kept from the old label it would name another product —
        // and decide the KYC of a sale it is not about.
        if (administration is not null && opportunity.Product != previousProduct)
            opportunity.SetProductLine(
                await ProductLines.ProductLineLookup.ResolveAsync(administration, opportunity.TenantId, opportunity.Product, ct));

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
