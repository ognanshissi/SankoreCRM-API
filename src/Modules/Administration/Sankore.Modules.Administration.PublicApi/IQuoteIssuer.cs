namespace Sankore.Modules.Administration.PublicApi;

/// <summary>
/// The institution as the issuer of its quotes (quote lines, lot 2, 2026-11-10): what the quote
/// PDF prints in its header and foot, the VAT rates and discount ceiling a quote line applies, and
/// the institution's numbering. Read by the Leads module, which owns projects and their quotes.
/// </summary>
public interface IQuoteIssuer
{
    /// <summary>
    /// The issuer of <paramref name="tenantId"/>'s quotes. A « Mode test » twin reads its live
    /// institution's identity — same team, same letterhead. An institution whose profile was never
    /// saved reads the defaults (18 %, 9 %, DEV, 30 days) and an empty identity.
    /// </summary>
    Task<QuoteIssuer> GetAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Takes the next quote number of the year of <paramref name="issuedOn"/> — DEV-2026-000001 —
    /// with the institution's prefix. Atomic across concurrent callers, committed on its own: a
    /// number taken by a command that then fails is not given back (a gap, never a duplicate).
    /// </summary>
    Task<string> NextNumberAsync(Guid tenantId, DateOnly issuedOn, CancellationToken ct);

    /// <summary>
    /// The catalogue products a quote line may name, by id, with what a line takes from them
    /// (quote lines, lot 3). Unknown ids are absent; inactive and non-Standard products are
    /// returned as they are, for the caller to refuse with the right message.
    /// </summary>
    Task<IReadOnlyList<QuotableProduct>> GetProductsAsync(Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken ct);

    /// <summary>The institution's logo, for the quote's header (lot 5); null when none was uploaded.</summary>
    Task<QuoteLogo?> GetLogoAsync(Guid tenantId, CancellationToken ct);
}

/// <param name="ContentType">image/png, image/jpeg or image/webp — what the upload accepts.</param>
public sealed record QuoteLogo(byte[] Content, string ContentType);

/// <param name="Category">The <c>ProductCategory</c> name; only « Standard » may be quoted.</param>
/// <param name="UnitPrice">Excluding VAT; null when the catalogue has no price.</param>
public sealed record QuotableProduct(
    Guid Id, string Code, string Name, string Category, bool IsActive,
    decimal? UnitPrice, string? PriceUnit, Sankore.Shared.Kernel.ProductVatCategory VatCategory)
{
    public bool IsStandard => Category == nameof(Sankore.Shared.Kernel.ProductCategory.Standard);
}

public sealed record QuoteIssuer(
    string Name,
    string? LegalName,
    string? LegalForm,
    string? HeadOfficeAddress,
    string? Phone,
    string? Email,
    string? Website,
    string? Rccm,
    string? TaxpayerNumber,
    string? TaxRegime,
    string? QuoteFooter,
    string Currency,
    decimal VatStandardRate,
    decimal VatReducedRate,
    decimal? MaxDiscountPercent,
    string QuotePrefix,
    int QuoteValidityDays,
    string? QuotePaymentTerms)
{
    /// <summary>The name a quote prints: the raison sociale, else the institution's name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(LegalName) ? Name : LegalName;
}
