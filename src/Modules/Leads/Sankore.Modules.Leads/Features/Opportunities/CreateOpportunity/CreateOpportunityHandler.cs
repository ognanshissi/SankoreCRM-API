namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

internal sealed class CreateOpportunityHandler(
    LeadsDbContext db,
    TimeProvider clock,
    Administration.PublicApi.IAdministrationModule? administration = null,
    ProjectTemplates.ProjectTemplateResolver? templates = null)
    : IRequestHandler<CreateOpportunityCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateOpportunityCommand cmd, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == cmd.LeadId, ct);
        if (!leadExists)
            return Result.Fail<Guid>("LEAD_NOT_FOUND");

        if (await ProjectTemplates.ProjectCloseDateRule.MissingAsync(templates, cmd.Product, cmd.ExpectedCloseDate, ct))
            return Result.Fail<Guid>(ProjectTemplateErrors.CloseDateRequired);

        Money? amount = cmd.EstimatedAmount.HasValue && cmd.EstimatedCurrency is not null
            ? new Money(cmd.EstimatedAmount.Value, cmd.EstimatedCurrency)
            : null;

        var opp = Opportunity.CreateFromLead(
            tenantId:          cmd.TenantId,
            leadId:            cmd.LeadId,
            title:             cmd.Title,
            product:           cmd.Product,
            ownerId:           cmd.OwnerId,
            clock:             clock,
            description:       cmd.Description,
            estimatedAmount:   amount,
            expectedCloseDate: cmd.ExpectedCloseDate);

        // The product's catalogue line, recorded with it: the sale's line is the project's (product
        // owner, 2026-10-24). Best-effort — a label the catalogue does not know stays « à préciser »
        // as far as the policy is concerned, and readers resolve again when null.
        if (administration is not null)
            opp.SetProductLine(await ProductLines.ProductLineLookup.ResolveAsync(administration, opp.TenantId, opp.Product, ct));

        db.Opportunities.Add(opp);
        await db.SaveChangesAsync(ct);

        return Result.Ok(opp.Id);
    }
}
