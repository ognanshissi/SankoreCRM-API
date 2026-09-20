namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

internal sealed class CreateOpportunityForCustomerHandler(
    LeadsDbContext db,
    TimeProvider clock,
    IPhoneBlindIndexer phoneBlindIndexer)
    : IRequestHandler<CreateOpportunityForCustomerCommand, Result<CreateOpportunityForCustomerResult>>
{
    public async Task<Result<CreateOpportunityForCustomerResult>> Handle(
        CreateOpportunityForCustomerCommand cmd, CancellationToken ct)
    {
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

        Money? amount = cmd.EstimatedAmount.HasValue && cmd.EstimatedCurrency is not null
            ? new Money(cmd.EstimatedAmount.Value, cmd.EstimatedCurrency)
            : null;

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

        db.Opportunities.Add(opp);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new CreateOpportunityForCustomerResult(
            OpportunityId:     opp.Id,
            DuplicateDetected: false));
    }
}
