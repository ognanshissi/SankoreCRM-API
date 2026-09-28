namespace Sankore.Modules.Customers.Features.Timeline.GetClientTimeline;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class GetClientTimelineHandler(
    CustomersDbContext db,
    IAgencyScopeProvider agencyScope,
    ICurrentUser currentUser)
    : IRequestHandler<GetClientTimelineQuery, Result<PagedResult<ClientTimelineEntryDto>>>
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedResult<ClientTimelineEntryDto>>> Handle(
        GetClientTimelineQuery request, CancellationToken ct)
    {
        // Unknown client and out-of-perimeter client answer the same thing, on purpose.
        var readable = await TimelineClientScope.CanReadClientAsync(
            db, agencyScope, currentUser, request.ClientId, ct);

        if (!readable)
            return Result.Fail<PagedResult<ClientTimelineEntryDto>>(CustomerErrors.ClientNotFound);

        var query = db.ClientTimelineEntries
            .Where(e => e.ClientId == request.ClientId);

        if (!string.IsNullOrWhiteSpace(request.SourceModule))
        {
            var sourceModule = request.SourceModule.Trim();
            query = query.Where(e => e.SourceModule == sourceModule);
        }

        if (!string.IsNullOrWhiteSpace(request.EntryType))
        {
            var entryType = request.EntryType.Trim();
            query = query.Where(e => e.EntryType == entryType);
        }

        var totalCount = await query.CountAsync(ct);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, MaxPageSize);

        var items = await query
            // Most recent first. Id is the tie-breaker so paging stays stable when several
            // facts share an instant (a merge re-parents entries with identical timestamps).
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new ClientTimelineEntryDto(
                e.Id,
                e.SourceModule,
                e.EntryType,
                e.OccurredAt,
                e.Summary,
                e.ReferenceType,
                e.ReferenceId))
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<ClientTimelineEntryDto>(items, totalCount, page, pageSize));
    }
}
