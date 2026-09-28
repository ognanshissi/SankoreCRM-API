namespace Sankore.Modules.Customers.Features.Lifecycle.GetStatusHistory;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetStatusHistoryHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope)
    : IRequestHandler<GetStatusHistoryQuery, Result<PagedResult<ClientStatusHistoryDto>>>
{
    public async Task<Result<PagedResult<ClientStatusHistoryDto>>> Handle(
        GetStatusHistoryQuery query, CancellationToken ct)
    {
        // Project only what the caller needs: the client row is loaded for the
        // perimeter check, not to be returned.
        var client = await db.Clients
            .Where(c => c.Id == query.ClientId)
            .Select(c => new { c.Id, c.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (client is null)
            return Result.Fail<PagedResult<ClientStatusHistoryDto>>(CustomerErrors.ClientNotFound);

        // Out of perimeter answers NOT_FOUND, never OUT_OF_SCOPE (no existence leak).
        if (!await agencyScope.CanAccessAgencyAsync(
                currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<PagedResult<ClientStatusHistoryDto>>(CustomerErrors.ClientNotFound);

        // Clamp before arithmetic: page 0 or -3 would otherwise produce a negative
        // Skip() and throw at the provider level.
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var baseQuery = db.ClientStatusHistories.Where(h => h.ClientId == query.ClientId);

        var total = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .OrderByDescending(h => h.OccurredAt)
            .ThenByDescending(h => h.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new ClientStatusHistoryDto(
                h.Id,
                h.OldStatus != null ? h.OldStatus.Value.ToString() : null,
                h.NewStatus.ToString(),
                h.Reason,
                h.ActorUserId,
                h.OccurredAt))
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<ClientStatusHistoryDto>(items, total, page, pageSize));
    }
}
