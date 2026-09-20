namespace Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetOpportunityHandler(LeadsDbContext db)
    : IRequestHandler<GetOpportunityQuery, Result<OpportunityDto>>
{
    public async Task<Result<OpportunityDto>> Handle(
        GetOpportunityQuery query, CancellationToken ct)
    {
        var opp = await db.Opportunities
            .Where(o => o.Id == query.OpportunityId)
            .Select(o => new OpportunityDto(
                o.Id, o.LeadId, o.CustomerEntityType, o.CustomerEntityId,
                o.Title, o.Description, o.Product, o.EstimatedAmount,
                o.Stage, o.Probability, o.ExpectedCloseDate, o.OwnerId,
                o.CreatedAt, o.UpdatedAt, o.ClosedAt, o.CloseReason))
            .FirstOrDefaultAsync(ct);

        return opp is null
            ? Result.Fail<OpportunityDto>("OPPORTUNITY_NOT_FOUND")
            : Result.Ok(opp);
    }
}
