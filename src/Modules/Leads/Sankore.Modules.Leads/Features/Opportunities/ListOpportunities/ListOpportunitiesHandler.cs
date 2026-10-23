namespace Sankore.Modules.Leads.Features.Opportunities.ListOpportunities;

using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Transactions;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ListOpportunitiesHandler(
    LeadsDbContext db,
    LeadVisibility? visibility = null,
    ICustomersModule? customers = null,
    ICurrentUser? currentUser = null)
    : IRequestHandler<ListOpportunitiesQuery, Result<IReadOnlyList<OpportunityDto>>>
{
    public async Task<Result<IReadOnlyList<OpportunityDto>>> Handle(
        ListOpportunitiesQuery query, CancellationToken ct)
    {
        var q = await ScopeAsync(query, ct);

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
                o.CreatedAt, o.UpdatedAt, o.ClosedAt, o.CloseReason,
                o.NextStep, o.NextStepDueOn, o.SeparateNeedConfirmedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<OpportunityDto>>(items);
    }

    /// <summary>
    /// An agent sees the opportunities of their leads and their own — and, on a client page, every
    /// opportunity of a client they may open: the client's advisor follows all of its business.
    /// </summary>
    private async Task<IQueryable<Opportunity>> ScopeAsync(ListOpportunitiesQuery query, CancellationToken ct)
    {
        if (visibility is null || visibility.SeesAll)
            return db.Opportunities;

        if (query.CustomerEntityId is { } clientId && customers is not null && currentUser is not null)
        {
            bool visible;
            // Suppressed: M01 reads through its own DbContext.
            using (new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
                visible = await customers.CanActorAccessClientAsync(currentUser.TenantId, clientId, currentUser.Id, ct);

            if (visible)
                return db.Opportunities;
        }

        return visibility.Apply(db.Opportunities, db.Leads);
    }
}
