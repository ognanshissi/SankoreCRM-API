namespace Sankore.Modules.Integration.Features.Insurance.QuoteInsuranceProduct;

using MediatR;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The premium for one product and one customer (ASS-03, criterion 2 — « Le tarif est soit fixe
/// dans le catalogue, soit calculé par l'assureur via IInsuranceProductPort si la capacité
/// existe »).
///
/// <para>
/// Both branches behind ONE question, because the caller — the subscription screen — must not
/// contain the branch. It asks "what does this cost", and whether the answer came out of the
/// catalogue or out of the insurer's tariff engine is reported in <see cref="QuoteSource"/> rather
/// than decided by the caller. A screen that had to choose would eventually choose wrong for one
/// of the two insurers a tenant distributes for.
/// </para>
///
/// <para>
/// A query and not an <c>ICommand</c>: it writes nothing here. The external call is journalled in
/// <c>integration_call_log</c> by the adapter itself, which is this module's audit of outbound
/// calls — an <c>ICommand</c> would open a transaction around a read and add an audit row that
/// duplicates it.
/// </para>
/// </summary>
internal sealed record QuoteInsuranceProductQuery(
    Guid ProductId,
    Guid CrmCustomerId,
    decimal? InsuredAmount = null)
    : IRequest<Result<InsuranceQuoteDto>>;

/// <summary>Where a premium came from. Reported, never inferred by the caller.</summary>
public enum QuoteSource
{
    /// <summary>Fixed in the tenant's catalogue. No insurer was called.</summary>
    Catalogue,

    /// <summary>Computed by the insurer through <c>IInsuranceProductPort.PriceAsync</c>.</summary>
    Insurer
}

/// <summary>A premium, and which of ASS-03's two branches produced it.</summary>
public sealed record InsuranceQuoteDto(
    Guid ProductId,
    string InsurerProductCode,
    decimal PremiumAmount,
    string Currency,
    PremiumPeriodicity Periodicity,
    QuoteSource Source,
    DateTimeOffset QuotedAt);
