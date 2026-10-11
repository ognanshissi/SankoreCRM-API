namespace Sankore.Modules.Customers.Features.Holdings.ListStalledHoldings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListStalledHoldingsHandler(CustomersDbContext db, TimeProvider clock)
    : IRequestHandler<ListStalledHoldingsQuery, Result<PagedResult<StalledHoldingDto>>>
{
    public async Task<Result<PagedResult<StalledHoldingDto>>> Handle(
        ListStalledHoldingsQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Negative is clamped rather than refused: a caller asking for "anything at all" means
        // zero, and a 400 for it would be pedantry on an operations screen.
        var threshold = now.AddHours(-Math.Max(0, query.MinAgeHours));

        // The tenant query filter does the isolation — no agency predicate, deliberately, unlike
        // ListHoldings. This list is read by whoever fixes an integration mapping, which is a
        // platform-level configuration: scoping it to an agency perimeter would hide from the
        // person who can fix the fault the very holdings it affects. It carries no client
        // identity for that reason.
        var stalled = db.ProductHoldings
            .Where(h => h.Status == HoldingStatus.PendingSync && h.UpdatedAt <= threshold);

        var total = await stalled.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var items = await stalled
            // Oldest first: the longest-stuck subscription is the one a client has been waiting
            // on, and the opposite order would bury it as the list grows.
            .OrderBy(h => h.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new
            {
                h.Id,
                h.ClientId,
                h.ProductLineId,
                h.ProductCode,
                h.ProductCategory,
                h.SignedAt,
                h.UpdatedAt,
            })
            .ToListAsync(ct);

        var rows = items
            .Select(h => new StalledHoldingDto(
                HoldingId: h.Id,
                ClientId: h.ClientId,
                ProductLineId: h.ProductLineId,
                ProductCode: h.ProductCode,
                ProductCategory: h.ProductCategory,
                SignedAt: h.SignedAt,
                UpdatedAt: h.UpdatedAt,
                StalledSinceHours: (int)(now - h.UpdatedAt).TotalHours))
            .ToList();

        return Result.Ok(new PagedResult<StalledHoldingDto>(rows, total, page, pageSize));
    }
}
