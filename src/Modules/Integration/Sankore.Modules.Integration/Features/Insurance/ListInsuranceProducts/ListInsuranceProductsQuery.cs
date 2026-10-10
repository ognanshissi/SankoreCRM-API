namespace Sankore.Modules.Integration.Features.Insurance.ListInsuranceProducts;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The tenant's insurance catalogue (ASS-03).
///
/// <para>
/// A query: no <c>ICommand</c>, so neither <c>TransactionBehavior</c> nor <c>AuditBehavior</c>
/// wraps it. Reading a catalogue is not an event worth an audit row.
/// </para>
/// </summary>
/// <param name="OfferableOnly">
/// Pre-filters on what the DATABASE can decide — the product's own flag, the connection's, and
/// the validity window. The insurer-pricing reason cannot be a SQL predicate (it needs an adapter
/// per row), so a listed product may still carry <c>INSURER_PRICING_UNAVAILABLE</c>. Callers
/// branch on <c>isOfferable</c> in the row, never on having passed this.
/// </param>
internal sealed record ListInsuranceProductsQuery(
    Guid? ConnectionId = null,
    bool? IsActive = null,
    bool OfferableOnly = false,
    int Page = 1,
    int PageSize = 50)
    : IRequest<Result<PagedResult<InsuranceProductDto>>>;
