namespace Sankore.Modules.Customers.Features.Holdings.ListHoldings;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// The products a client holds (BNF-B-14), newest first, filterable by category and status.
/// </summary>
/// <param name="Category">
/// A <c>ProductCategory</c> name, matched against the category SNAPSHOT taken at subscription — not
/// against the catalogue today, so a product recategorised since does not move between filters on a
/// screen listing contracts.
/// </param>
/// <param name="Status">A <c>HoldingStatus</c> name, or null for every status.</param>
public sealed record ListHoldingsQuery(
    Guid ClientId,
    string? Category,
    string? Status,
    int Page,
    int PageSize) : IRequest<Result<PagedResult<HoldingDto>>>;
