namespace Sankore.Modules.Customers.Features.Holdings.ListHoldings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class ListHoldingsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    SubjectLabelResolver labels)
    : IRequestHandler<ListHoldingsQuery, Result<PagedResult<HoldingDto>>>
{
    public async Task<Result<PagedResult<HoldingDto>>> Handle(
        ListHoldingsQuery query, CancellationToken ct)
    {
        var client = await db.Clients
            .Where(c => c.Id == query.ClientId)
            .Select(c => new { c.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<PagedResult<HoldingDto>>(CustomerErrors.ClientNotFound);

        HoldingStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<HoldingStatus>(query.Status, ignoreCase: true, out var parsed))
                return Result.Fail<PagedResult<HoldingDto>>("INVALID_HOLDING_STATUS");

            status = parsed;
        }

        var category = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim();

        var holdings = db.ProductHoldings
            .Include(h => h.Designations)
            .Where(h => h.ClientId == query.ClientId
                     && (status == null || h.Status == status)
                     && (category == null || h.ProductCategory == category));

        var total = await holdings.CountAsync(ct);

        // Page clamped to 1 and above, so a negative page cannot produce a negative Skip — the
        // pagination trap this codebase has paid for before.
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var items = await holdings
            .OrderByDescending(h => h.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // ONE pair of queries for every designation on the page — see SubjectLabelResolver.
        var resolved = await labels.ResolveAsync(
            items.SelectMany(h => h.CurrentDesignations), ct);

        return Result.Ok(new PagedResult<HoldingDto>(
            [.. items.Select(h => HoldingMapping.ToDto(h, resolved))],
            total, page, pageSize));
    }
}
