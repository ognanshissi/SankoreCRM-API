namespace Sankore.Modules.Customers.Features.Compliance.Retention.ListRetentionCandidates;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class ListRetentionCandidatesHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope
) : IRequestHandler<ListRetentionCandidatesQuery, Result<PagedResult<RetentionCandidateDto>>>
{
    public async Task<Result<PagedResult<RetentionCandidateDto>>> Handle(
        ListRetentionCandidatesQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        // The proposals live in the timeline, so the eligible set is "every client the monthly
        // scan has ever tagged". The population is tiny by construction (archived more than ten
        // years ago), which is what makes the client-side Contains below acceptable.
        var eligibleClientIds = await db.ClientTimelineEntries
            .Where(e => e.SourceModule == RetentionWindow.TimelineSourceModule
                        && e.EntryType == RetentionWindow.RetentionEligibleEntryType)
            .Select(e => e.ClientId)
            .Distinct()
            .ToListAsync(ct);

        if (eligibleClientIds.Count == 0)
            return Result.Ok(new PagedResult<RetentionCandidateDto>([], 0, page, pageSize));

        var clients = db.Clients
            .Where(c => eligibleClientIds.Contains(c.Id)
                        && c.Status == ClientStatus.Archived
                        && !c.IsAnonymized);

        // Agency perimeter: null means unrestricted (super-user), an empty set means the caller
        // sees nothing — never treat null as "no access".
        var accessible = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        if (accessible is not null)
            clients = clients.Where(c => accessible.Contains(c.AgencyId));

        var total = await clients.CountAsync(ct);

        // Oldest archive first: the client that has waited longest is the one to deal with.
        var pageItems = await clients
            .OrderBy(c => c.ArchivedAt)
            .ThenBy(c => c.ClientNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.ClientNumber,
                c.Type,
                c.AgencyId,
                c.AgencyCode,
                c.ArchivedAt,
                c.ArchiveReason
            })
            .ToListAsync(ct);

        var pageClientIds = pageItems.Select(c => c.Id).ToList();

        // Most recent proposal per client on this page only — the job re-proposes monthly until
        // someone acts, and the freshest date is the one worth showing.
        var identifiedAt = await db.ClientTimelineEntries
            .Where(e => e.SourceModule == RetentionWindow.TimelineSourceModule
                        && e.EntryType == RetentionWindow.RetentionEligibleEntryType
                        && pageClientIds.Contains(e.ClientId))
            .GroupBy(e => e.ClientId)
            .Select(g => new { ClientId = g.Key, LastAt = g.Max(e => e.OccurredAt) })
            .ToDictionaryAsync(x => x.ClientId, x => x.LastAt, ct);

        var items = pageItems
            .Select(c => new RetentionCandidateDto(
                ClientId: c.Id,
                ClientNumber: c.ClientNumber,
                ClientType: c.Type.ToString(),
                AgencyId: c.AgencyId,
                AgencyCode: c.AgencyCode,
                ArchivedAt: c.ArchivedAt,
                ArchiveReason: c.ArchiveReason,
                IdentifiedAt: identifiedAt.TryGetValue(c.Id, out var at) ? at : default))
            .ToList();

        return Result.Ok(new PagedResult<RetentionCandidateDto>(items, total, page, pageSize));
    }
}
