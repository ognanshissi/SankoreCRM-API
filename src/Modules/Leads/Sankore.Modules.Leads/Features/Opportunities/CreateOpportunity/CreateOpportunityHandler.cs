namespace Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

internal sealed class CreateOpportunityHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<CreateOpportunityCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateOpportunityCommand cmd, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == cmd.LeadId, ct);
        if (!leadExists)
            return Result.Fail<Guid>("LEAD_NOT_FOUND");

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

        db.Opportunities.Add(opp);
        await db.SaveChangesAsync(ct);

        return Result.Ok(opp.Id);
    }
}
