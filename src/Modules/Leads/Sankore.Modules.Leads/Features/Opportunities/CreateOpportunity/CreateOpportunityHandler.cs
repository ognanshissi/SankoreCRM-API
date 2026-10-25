namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

internal sealed class CreateOpportunityHandler(
    LeadsDbContext db,
    TimeProvider clock,
    Administration.PublicApi.IAdministrationModule? administration = null,
    ProjectTemplates.ProjectTemplateResolver? templates = null,
    Administration.PublicApi.IInstitutionCurrency? institutionCurrency = null,
    Customers.PublicApi.ICustomersModule? customers = null)
    : IRequestHandler<CreateOpportunityCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateOpportunityCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .Where(l => l.Id == cmd.LeadId)
            .Select(l => new { ClientId = l.ConvertedToCustomerId ?? l.CustomerId })
            .FirstOrDefaultAsync(ct);
        if (lead is null)
            return Result.Fail<Guid>("LEAD_NOT_FOUND");

        // Data-purge lot 3: no prospection of a person who asked to be forgotten.
        if (await Retention.ProcessingRestriction.IsRestrictedAsync(customers, cmd.TenantId, lead.ClientId, ct))
            return Result.Fail<Guid>(Retention.ProcessingRestriction.Error);

        if (await ProjectTemplates.ProjectCloseDateRule.MissingAsync(templates, cmd.Product, cmd.ExpectedCloseDate, ct))
            return Result.Fail<Guid>(ProjectTemplateErrors.CloseDateRequired);

        // In the institution's currency: there is no other to choose.
        Money? amount = null;
        if (cmd.EstimatedAmount is { } estimated)
        {
            var currency = await institutionCurrency.ResolveAsync(cmd.TenantId, cmd.EstimatedCurrency, stored: null, ct);
            if (currency.IsFailure) return Result.Fail<Guid>(currency.Error!);
            amount = new Money(estimated, currency.Value);
        }

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
