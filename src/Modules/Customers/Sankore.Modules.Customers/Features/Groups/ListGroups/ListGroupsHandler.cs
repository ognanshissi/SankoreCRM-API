namespace Sankore.Modules.Customers.Features.Groups.ListGroups;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class ListGroupsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope
) : IRequestHandler<ListGroupsQuery, Result<PagedResult<GroupListItemDto>>>
{
    private const int MaxPageSize = 200;

    public async Task<Result<PagedResult<GroupListItemDto>>> Handle(
        ListGroupsQuery request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, MaxPageSize);

        // null = unrestricted (super-user / tenant-wide account) => no agency filter.
        // An EMPTY set is the opposite and must filter everything out.
        var accessible = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        var query = db.ClientGroups.AsQueryable();

        if (accessible is not null)
        {
            var allowed = accessible.ToList();
            query = query.Where(g => allowed.Contains(g.AgencyId));
        }

        if (request.AgencyId.HasValue)
            query = query.Where(g => g.AgencyId == request.AgencyId.Value);

        if (request.Type.HasValue)
            query = query.Where(g => g.Type == request.Type.Value);

        if (request.Status.HasValue)
            query = query.Where(g => g.Status == request.Status.Value);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var needle = request.Search.Trim().ToLower();
            query = query.Where(g => g.Name.ToLower().Contains(needle));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderBy(g => g.Name)
            .ThenBy(g => g.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new GroupListItemDto(
                g.Id,
                g.Name,
                g.Type.ToString(),
                g.Status.ToString(),
                g.AgencyId,
                g.ConstitutionDate,
                // ActiveMemberCount is [NotMapped] on the aggregate, so it is counted
                // in SQL here instead of materialising every membership row.
                g.Memberships.Count(m => m.LeftAt == null),
                g.CreatedAt,
                g.UpdatedAt))
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<GroupListItemDto>(items, totalCount, page, pageSize));
    }
}
