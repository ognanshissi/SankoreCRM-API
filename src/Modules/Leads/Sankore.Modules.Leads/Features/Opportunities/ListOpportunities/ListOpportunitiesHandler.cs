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
    ICurrentUser? currentUser = null,
    ProjectTemplates.ProjectTemplateResolver? templates = null,
    ITenantContext? tenant = null)
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
                o.NextStep, o.NextStepDueOn, o.SeparateNeedConfirmedAt, o.StepId)
            {
                TemplateVersionId = o.TemplateVersionId,
            })
            .ToListAsync(ct);

        IReadOnlyList<OpportunityDto> enriched = templates is null ? items : await items.WithStepsAsync(templates, ct);
        enriched = await enriched.WithConditionsAsync(db, workflows: null, ct);
        return Result.Ok(await WithContactsAsync(enriched, ct));
    }

    /// <summary>
    /// Who each project is for (the projects board searches it). One query for the leads, one batch
    /// call to M01 for the clients without a lead — never one per row.
    /// </summary>
    private async Task<IReadOnlyList<OpportunityDto>> WithContactsAsync(
        IReadOnlyList<OpportunityDto> items, CancellationToken ct)
    {
        var leadIds = items.Where(o => o.LeadId != null).Select(o => o.LeadId!.Value).Distinct().ToList();
        var leads = leadIds.Count == 0
            ? new Dictionary<Guid, ProjectContactDto>()
            : (await db.Leads.AsNoTracking()
                    .Where(l => leadIds.Contains(l.Id))
                    .Select(l => new { l.Id, l.FullName, l.FirstName, l.LastName, l.PhoneNumber, l.Email, l.LegacyEmailClear })
                    .ToListAsync(ct))
                .ToDictionary(
                    l => l.Id,
                    l => new ProjectContactDto(l.FullName, l.FirstName, l.LastName, l.PhoneNumber, l.Email ?? l.LegacyEmailClear, null));

        var clientIds = items
            .Where(o => o.LeadId == null && o.CustomerEntityId != null)
            .Select(o => o.CustomerEntityId!.Value)
            .Distinct()
            .ToList();

        IReadOnlyDictionary<Guid, ClientSummary> clients = new Dictionary<Guid, ClientSummary>();
        if (clientIds.Count > 0 && customers is not null && tenant is not null)
        {
            try
            {
                // Suppressed: M01 reads through its own DbContext.
                using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                clients = await customers.GetClientSummariesAsync(tenant.CurrentTenantId, clientIds, ct);
                suppressed.Complete();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A list without names is still a list: the board falls back to the project's title.
                _ = ex;
            }
        }

        return items.Select(o =>
        {
            if (o.LeadId is { } leadId && leads.TryGetValue(leadId, out var fromLead))
                return o with { Contact = fromLead };
            if (o.CustomerEntityId is { } clientId && clients.TryGetValue(clientId, out var client))
                return o with { Contact = new ProjectContactDto(client.DisplayName, null, null, null, null, client.ClientNumber) };
            return o;
        }).ToList();
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
