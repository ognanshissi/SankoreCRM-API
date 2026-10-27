namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

internal sealed class CreateOpportunityForCustomerHandler(
    LeadsDbContext db,
    TimeProvider clock,
    IPhoneBlindIndexer phoneBlindIndexer,
    Administration.PublicApi.IAdministrationModule? administration = null,
    ProjectTemplates.ProjectTemplateResolver? templates = null,
    Administration.PublicApi.IInstitutionCurrency? institutionCurrency = null,
    Customers.PublicApi.ICustomersModule? customers = null,
    History.ProjectHistoryRecorder? history = null)
    : IRequestHandler<CreateOpportunityForCustomerCommand, Result<CreateOpportunityForCustomerResult>>
{
    public async Task<Result<CreateOpportunityForCustomerResult>> Handle(
        CreateOpportunityForCustomerCommand cmd, CancellationToken ct)
    {
        if (await ProjectTemplates.ProjectCloseDateRule.MissingAsync(templates, cmd.Product, cmd.ExpectedCloseDate, ct))
            return Result.Fail<CreateOpportunityForCustomerResult>(ProjectTemplateErrors.CloseDateRequired);

        // Data-purge lot 3: no prospection of a person who asked to be forgotten.
        if (await Retention.ProcessingRestriction.IsRestrictedAsync(customers, cmd.TenantId, cmd.CustomerEntityId, ct))
            return Result.Fail<CreateOpportunityForCustomerResult>(Retention.ProcessingRestriction.Error);

        // ── Duplicate check: existing open opportunities for the same customer + product ──
        if (!cmd.Force)
        {
            var existingIds = await db.Opportunities
                .Where(o => o.CustomerEntityId == cmd.CustomerEntityId
                         && o.Product == cmd.Product
                         && o.Stage != OpportunityStage.ClosedWon
                         && o.Stage != OpportunityStage.ClosedLost)
                .Select(o => o.Id)
                .ToListAsync(ct);

            if (existingIds.Count > 0)
            {
                return Result.Ok(new CreateOpportunityForCustomerResult(
                    OpportunityId:          null,
                    DuplicateDetected:      true,
                    ExistingOpportunityIds: existingIds));
            }
        }

        // ── Phone blind-index dedup against leads (reuse US-M13-010) ─────
        if (!cmd.Force && cmd.CustomerPhone is not null)
        {
            var phoneIndex = phoneBlindIndexer.Compute(cmd.CustomerPhone);
            var matchingLeadExists = await db.Leads
                .AnyAsync(l => l.PhoneBlindIndex == phoneIndex
                            && l.Status != LeadStatus.Lost
                            && l.Status != LeadStatus.Archived
                            && l.Status != LeadStatus.Disqualified, ct);

            // Not blocking — just informational; the opportunity is for a known customer.
            // Could be extended to return matched lead IDs if needed.
        }

        // In the institution's currency: there is no other to choose.
        Money? amount = null;
        if (cmd.EstimatedAmount is { } estimated)
        {
            var currency = await institutionCurrency.ResolveAsync(cmd.TenantId, cmd.EstimatedCurrency, stored: null, ct);
            if (currency.IsFailure) return Result.Fail<CreateOpportunityForCustomerResult>(currency.Error!);
            amount = new Money(estimated, currency.Value);
        }

        var opp = Opportunity.CreateForCustomer(
            tenantId:          cmd.TenantId,
            customerEntityId:  cmd.CustomerEntityId,
            title:             cmd.Title,
            product:           cmd.Product,
            ownerId:           cmd.OwnerId,
            clock:             clock,
            leadId:            cmd.LeadId,
            description:       cmd.Description,
            estimatedAmount:   amount,
            expectedCloseDate: cmd.ExpectedCloseDate);

        if (administration is not null)
            opp.SetProductLine(await ProductLines.ProductLineLookup.ResolveAsync(administration, opp.TenantId, opp.Product, ct));

        await ProjectTemplates.ProjectFirstStep.PlaceAsync(templates, opp, clock.GetUtcNow(), ct);

        db.Opportunities.Add(opp);
        history?.Created(opp);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new CreateOpportunityForCustomerResult(
            OpportunityId:     opp.Id,
            DuplicateDetected: false));
    }
}
