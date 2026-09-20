namespace Sankore.Modules.Leads.Features.Opportunities.ListOpportunities;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListOpportunitiesHandler(LeadsDbContext db)
    : IRequestHandler<ListOpportunitiesQuery, Result<IReadOnlyList<OpportunityDto>>>
{
    public async Task<Result<IReadOnlyList<OpportunityDto>>> Handle(
        ListOpportunitiesQuery query, CancellationToken ct)
    {
        var q = db.Opportunities.AsQueryable();

        if (query.LeadId.HasValue)
            q = q.Where(o => o.LeadId == query.LeadId);

        if (query.CustomerEntityId.HasValue)
            q = q.Where(o => o.CustomerEntityId == query.CustomerEntityId);

        if (query.Stage.HasValue)
            q = q.Where(o => o.Stage == query.Stage);

        var items = await q
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OpportunityDto(
                o.Id, o.LeadId, o.CustomerEntityType, o.CustomerEntityId,
                o.Title, o.Description, o.Product, o.EstimatedAmount,
                o.Stage, o.Probability, o.ExpectedCloseDate, o.OwnerId,
                o.CreatedAt, o.UpdatedAt, o.ClosedAt, o.CloseReason))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<OpportunityDto>>(items);
    }
}
