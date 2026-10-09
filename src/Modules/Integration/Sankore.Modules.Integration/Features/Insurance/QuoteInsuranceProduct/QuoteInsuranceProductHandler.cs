namespace Sankore.Modules.Integration.Features.Insurance.QuoteInsuranceProduct;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class QuoteInsuranceProductHandler(
    IntegrationDbContext db,
    IntegrationAdapterResolver resolver,
    TimeProvider clock,
    ILogger<QuoteInsuranceProductHandler> logger)
    : IRequestHandler<QuoteInsuranceProductQuery, Result<InsuranceQuoteDto>>
{
    public async Task<Result<InsuranceQuoteDto>> Handle(
        QuoteInsuranceProductQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var found = await db.FindWithConnectionAsync(query.ProductId, ct);

        if (found is null)
            return Result.Fail<InsuranceQuoteDto>(IntegrationErrors.InsuranceProductNotFound);

        var (product, connection) = found.Value;

        // Offerability FIRST, and the full verdict — not just the product's own flag. Quoting a
        // product whose insurer connection is deactivated would hand an agent a price for
        // something that cannot be subscribed, and that price is what they would read out to the
        // customer. The insurer-pricing reason is excluded from this gate because for an
        // insurer-priced product it IS the next check, and reporting it twice under two codes
        // would make a missing capability look like two different faults.
        var blockers = ProductOfferability.Reasons(
            productIsActive: product.IsActive,
            connectionIsActive: connection.IsActive,
            connectionIsInsuranceFamily: connection.Family == IntegrationFamily.Insurance,
            effectiveFrom: product.EffectiveFrom,
            effectiveTo: product.EffectiveTo,
            pricingMode: product.PricingMode,
            insurerPricingAvailable: true,
            today: DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        if (blockers.Count > 0)
            return Result.Fail<InsuranceQuoteDto>(
                $"{IntegrationErrors.InsuranceProductNotOfferable}: {string.Join(", ", blockers)}");

        return product.PricingMode == ProductPricingMode.CatalogueFixed
            ? CatalogueQuote(product)
            : await InsurerQuote(product, connection, query, ct);
    }

    /// <summary>
    /// The catalogue branch. No insurer call at all — a tenant that prices its own products needs
    /// no capability, which is why ASS-03 declares the capability rather than assuming it.
    /// </summary>
    private Result<InsuranceQuoteDto> CatalogueQuote(InsuranceProduct product)
        => Result.Ok(new InsuranceQuoteDto(
            ProductId: product.Id,
            InsurerProductCode: product.InsurerProductCode,
            // Non-null by the aggregate's own invariant: a CatalogueFixed product cannot exist
            // without a positive premium and a currency.
            PremiumAmount: product.FixedPremiumAmount!.Value,
            Currency: product.Currency!,
            Periodicity: product.Periodicity,
            Source: QuoteSource.Catalogue,
            QuotedAt: clock.GetUtcNow()));

    private async Task<Result<InsuranceQuoteDto>> InsurerQuote(
        InsuranceProduct product,
        IntegrationConnection connection,
        QuoteInsuranceProductQuery query,
        CancellationToken ct)
    {
        // ResolveFor, not ResolveAdapter: the capability check inside ResolvePort is a question
        // about THIS insurer's installation, and this tenant may distribute for another insurer of
        // the same kind whose matrix differs.
        var adapter = resolver.ResolveFor(connection);

        if (adapter.IsFailure)
            return Result.Fail<InsuranceQuoteDto>($"{adapter.Code}: {adapter.Detail}");

        var port = resolver.ResolvePort<IInsuranceProductPort>(
            adapter.Value, IntegrationCapability.PriceProduct);

        // The capability is declared per adapter and per installation, so this is the honest
        // answer and not a defect: this insurer does not price through us, and the product's
        // configuration has to say a fixed premium instead.
        if (port.IsFailure)
            return Result.Fail<InsuranceQuoteDto>($"{port.Code}: {port.Detail}");

        var quote = await port.Value.PriceAsync(
            product.InsurerProductCode, query.CrmCustomerId, query.InsuredAmount, ct);

        if (quote.IsFailure)
        {
            // The adapter's own code and family travel out unchanged. A transient insurer outage
            // and a refused product are different things for the agent at the counter, and
            // flattening both into one local code would lose exactly that distinction.
            logger.LogWarning(
                "Insurer {Kind} could not price {ProductCode} ({Family}/{Code})",
                connection.Kind, product.InsurerProductCode, quote.Family, quote.Code);

            return Result.Fail<InsuranceQuoteDto>($"{quote.Code}: {quote.Detail}");
        }

        var priced = quote.Value;

        return Result.Ok(new InsuranceQuoteDto(
            ProductId: product.Id,
            InsurerProductCode: product.InsurerProductCode,
            PremiumAmount: priced.PremiumAmount,
            Currency: priced.Currency,
            // The insurer's own periodicity wins over the catalogue's: it is the one the premium
            // it just quoted applies to, and a monthly figure shown as annual is a tenfold error.
            Periodicity: priced.Periodicity,
            Source: QuoteSource.Insurer,
            QuotedAt: priced.QuotedAt));
    }
}
