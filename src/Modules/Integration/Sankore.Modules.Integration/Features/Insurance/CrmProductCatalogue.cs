namespace Sankore.Modules.Integration.Features.Insurance;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Where an insurance product stands relative to the CRM catalogue entry it names.
///
/// <para>
/// Four states and not a boolean, because the three non-trivial ones are three different sentences
/// for an administrator: nothing to check, a product the institution has stopped selling, and a
/// code that resolves to nothing at all.
/// </para>
/// </summary>
internal enum CrmCatalogueStatus
{
    /// <summary>
    /// The product names no catalogue entry. Every product configured before
    /// <c>ins_product.crm_product_code</c> existed is in this state, and it is not a fault: the
    /// product is sellable, it is simply absent from the CRM's product-by-code reporting.
    /// </summary>
    NotLinked,

    /// <summary>The catalogue entry resolves and the institution still sells it.</summary>
    Active,

    /// <summary>
    /// The catalogue entry resolves and has been retired in M12. The institution stopped selling
    /// the product; new subscriptions must stop with it.
    /// </summary>
    Withdrawn,

    /// <summary>
    /// The code resolves to nothing in this tenant's catalogue. Unreachable through the API — the
    /// create and update handlers refuse it — so this means a row written before the check
    /// existed, or by hand.
    /// </summary>
    Unknown,
}

/// <summary>
/// The CRM catalogue entry an insurance product realises (<c>ins_product.crm_product_code</c>),
/// both halves in one place: the refusal at write time and the verdict at read time.
///
/// <para>
/// <b>Why one class.</b> The two halves ask M12 the same question about the same code and must
/// agree on what counts as an insurance product — a write that accepts a savings code and a read
/// that then reports the product unofferable would be a configuration screen contradicting itself.
/// The caching is the same bargain <see cref="InsurerPricingProbe"/> makes one level down: that one
/// caches an adapter per connection for a request, this one caches a catalogue lookup per code,
/// which is what keeps a page of fifty products spanning five catalogue entries at five calls
/// instead of fifty.
/// </para>
///
/// <para>
/// Reached through <c>IAdministrationModule</c> and never M12's assembly, by code and never a
/// foreign key — the rule <see cref="LinkedCreditProductCheck"/> states at length. The only
/// difference between the two is which categories they accept: that one demands a loan, this one
/// demands an insurance product.
/// </para>
/// </summary>
internal sealed class CrmProductCatalogue(
    IAdministrationModule administration,
    ITenantContext tenant,
    ILogger<CrmProductCatalogue> logger)
{
    /// <summary>
    /// The categories of M12's catalogue an insurance distribution agreement may realise.
    ///
    /// <para>
    /// Three and not one: <see cref="ProductCategory"/> carries <c>Insurance</c> alongside
    /// <c>HealthInsurance</c> and <c>ForecastInsurance</c> (<i>prévoyance</i>), which are insurance
    /// products an IMF distributes under exactly this kind of agreement. Accepting only
    /// <c>Insurance</c> would refuse a health cover for having been described precisely.
    /// </para>
    /// </summary>
    private static readonly ProductCategory[] InsuranceCategories =
    [
        ProductCategory.Insurance,
        ProductCategory.HealthInsurance,
        ProductCategory.ForecastInsurance,
    ];

    /// <summary>
    /// Per request, keyed by the upper-cased code. A null value is a cached MISS — the lookup
    /// happened and resolved to nothing — which is why this is not a
    /// <c>Dictionary&lt;string, ProductSummary&gt;</c> consulted with <c>ContainsKey</c>.
    /// </summary>
    private readonly Dictionary<string, ProductSummary?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Success for a blank code — a product need not name a catalogue entry — and a named failure
    /// for one that does not resolve or is not an insurance product.
    ///
    /// <para>
    /// The category check is the half that earns its keep. An agreement pointed at a loan code is
    /// not a typo anyone catches downstream: the product would appear in the CRM's loan reporting,
    /// a qualification template meant for credit would resolve for it, and the commission would be
    /// booked against a product the institution does not insure.
    /// </para>
    /// </summary>
    internal async Task<Result> VerifyAsync(string? crmProductCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(crmProductCode)) return Result.Ok();

        var code = Normalise(crmProductCode);
        var product = await LookupAsync(code, ct);

        if (product is null)
        {
            logger.LogInformation(
                "Insurance product refused: CRM product {Code} is unknown to the tenant's catalogue",
                code);

            return Result.Fail(
                $"{IntegrationErrors.InsuranceCrmProductInvalid}: no product '{code}' exists in "
                + "this tenant's catalogue.");
        }

        if (!IsInsurance(product.Category))
            return Result.Fail(
                $"{IntegrationErrors.InsuranceCrmProductInvalid}: product '{code}' is a "
                + $"{product.Category} product, and an insurance distribution agreement can only "
                + "realise an insurance catalogue entry.");

        // Deliberately NOT refused for being retired. An administrator preparing next year's
        // agreement before re-activating the catalogue entry is ordinary work, and the product is
        // created inactive anyway; the retirement surfaces as an offerability reason, which is
        // where a fact that goes stale by another row changing belongs.
        return Result.Ok();
    }

    /// <summary>
    /// The read-time verdict for one code. Cached, so a handler and the DTO it builds ask M12 once
    /// per distinct code.
    /// </summary>
    internal async Task<CrmCatalogueStatus> StatusAsync(string? crmProductCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(crmProductCode)) return CrmCatalogueStatus.NotLinked;

        return Classify(await LookupAsync(Normalise(crmProductCode), ct));
    }

    /// <summary>
    /// Resolves every distinct code of a page in one pass, so a caller can then build its DTOs
    /// synchronously with <see cref="StatusOf"/>.
    ///
    /// <para>
    /// One lookup per distinct code, bounded by the catalogue entries a page of products spans — a
    /// handful, not thousands. The same shape, and the same reservation, as
    /// <c>ContractCrmCodeCatalog.AgenciesAsync</c>: a batch projection would be the right fix and
    /// it belongs on the Administration contract, not here.
    /// </para>
    /// </summary>
    internal async Task PrefetchAsync(IEnumerable<string?> crmProductCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(crmProductCodes);

        var codes = crmProductCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Normalise(c!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var code in codes)
            await LookupAsync(code, ct);
    }

    /// <summary>
    /// The verdict for a code already resolved by <see cref="PrefetchAsync"/>.
    ///
    /// <para>
    /// A code that was never prefetched answers <see cref="CrmCatalogueStatus.NotLinked"/> rather
    /// than reaching M12 synchronously or throwing: the alternatives are a blocking call inside a
    /// projection and a catalogue screen that 500s on one row. It reads as "not checked", which is
    /// the honest answer, and the only way to reach it is a caller that forgot the prefetch — which
    /// a test pins.
    /// </para>
    /// </summary>
    internal CrmCatalogueStatus StatusOf(string? crmProductCode)
    {
        if (string.IsNullOrWhiteSpace(crmProductCode)) return CrmCatalogueStatus.NotLinked;

        return _cache.TryGetValue(Normalise(crmProductCode), out var product)
            ? Classify(product)
            : CrmCatalogueStatus.NotLinked;
    }

    /// <summary>Upper-cased, the way M12 stores and matches its codes.</summary>
    private static string Normalise(string code) => code.Trim().ToUpperInvariant();

    private static CrmCatalogueStatus Classify(ProductSummary? product)
        => product switch
        {
            null => CrmCatalogueStatus.Unknown,
            { IsActive: false } => CrmCatalogueStatus.Withdrawn,
            _ => CrmCatalogueStatus.Active,
        };

    private static bool IsInsurance(string category)
        => Enum.TryParse<ProductCategory>(category, ignoreCase: true, out var parsed)
           && InsuranceCategories.Contains(parsed);

    private async Task<ProductSummary?> LookupAsync(string code, CancellationToken ct)
    {
        if (_cache.TryGetValue(code, out var cached)) return cached;

        var product = await administration.GetProductAsync(tenant.CurrentTenantId, code, ct);

        _cache[code] = product;
        return product;
    }
}
