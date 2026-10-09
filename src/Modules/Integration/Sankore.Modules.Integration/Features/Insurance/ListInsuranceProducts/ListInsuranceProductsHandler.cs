namespace Sankore.Modules.Integration.Features.Insurance.ListInsuranceProducts;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListInsuranceProductsHandler(
    IntegrationDbContext db,
    InsurerPricingProbe pricing,
    TimeProvider clock)
    : IRequestHandler<ListInsuranceProductsQuery, Result<PagedResult<InsuranceProductDto>>>
{
    private const int MaxPageSize = 200;

    public async Task<Result<PagedResult<InsuranceProductDto>>> Handle(
        ListInsuranceProductsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // Joined rather than loaded separately: the connection decides half of ASS-03's
        // offerability, so a page of products without its connections could not be rendered. No
        // tenant predicate — both sides carry the global query filter.
        //
        // An ANONYMOUS projection, deliberately. A named record here does not translate: EF turns
        // `OrderBy(r => new ProductRow(a, b).Connection.Name)` into a construction it cannot read
        // through, and the query fails at run time rather than at compile time. The anonymous type
        // stays a transparent identifier, which the provider does see through. Reassigning `rows`
        // below is fine — the type is fixed by the initialiser.
        var rows = from p in db.InsuranceProducts
                   join c in db.Connections on p.ConnectionId equals c.Id
                   select new { Product = p, Connection = c };

        if (query.ConnectionId is { } connectionId)
            rows = rows.Where(r => r.Product.ConnectionId == connectionId);

        if (query.IsActive is { } isActive)
            rows = rows.Where(r => r.Product.IsActive == isActive);

        if (query.OfferableOnly)
            rows = rows.Where(r =>
                r.Product.IsActive
                && r.Connection.IsActive
                && r.Product.EffectiveFrom <= today
                && (r.Product.EffectiveTo == null || r.Product.EffectiveTo >= today));

        var total = await rows.CountAsync(ct);

        var items = await rows
            // Grouped by insurer, then alphabetical: the screen's first question is "what do I
            // sell for ORASS". Id last, so paging is stable when two products share a name.
            .OrderBy(r => r.Connection.Name)
            .ThenBy(r => r.Product.Name)
            .ThenBy(r => r.Product.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // The capability is resolved here, out of the query, and cached per connection by the
        // probe: a page of fifty products typically spans two or three insurers.
        var dtos = items
            .Select(r => InsuranceProductDto.From(
                r.Product, r.Connection, pricing.CanPrice(r.Connection), today))
            .ToList();

        return Result.Ok(new PagedResult<InsuranceProductDto>(dtos, total, page, pageSize));
    }
}
