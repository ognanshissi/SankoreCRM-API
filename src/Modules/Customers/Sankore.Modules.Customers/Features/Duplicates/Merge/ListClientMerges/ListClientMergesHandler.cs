namespace Sankore.Modules.Customers.Features.Duplicates.Merge.ListClientMerges;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Lists merge requests whose two clients are both inside the caller's agency perimeter — the same
/// rule as the duplicate queue, for the same reason: half a pair is not actionable.
/// </summary>
internal sealed class ListClientMergesHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope)
    : IRequestHandler<ListClientMergesQuery, Result<PagedResult<ClientMergeRequestDto>>>
{
    public async Task<Result<PagedResult<ClientMergeRequestDto>>> Handle(
        ListClientMergesQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var accessible = await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, currentUser.Id, ct);

        var clients = db.Clients.IgnoreQueryFilters().Where(c => c.TenantId == tenantId);

        var rows = from r in db.ClientMergeRequests.IgnoreQueryFilters().Where(r => r.TenantId == tenantId)
                   join survivor in clients on r.SurvivorClientId equals survivor.Id
                   join absorbed in clients on r.AbsorbedClientId equals absorbed.Id
                   select new { Request = r, Survivor = survivor, Absorbed = absorbed };

        if (query.Status is not null)
            rows = rows.Where(x => x.Request.Status == query.Status);

        if (accessible is not null)
        {
            var agencyIds = accessible.ToList();
            rows = rows.Where(x => agencyIds.Contains(x.Survivor.AgencyId)
                                && agencyIds.Contains(x.Absorbed.AgencyId));
        }

        var total = await rows.CountAsync(ct);

        var materialized = await rows
            .OrderByDescending(x => x.Request.RequestedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = materialized
            .Select(x => ClientMergeRequestMapper.ToDto(x.Request, x.Survivor, x.Absorbed))
            .ToList();

        return Result.Ok(new PagedResult<ClientMergeRequestDto>(items, total, page, pageSize));
    }
}
