using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

public class CompanyInfo
{
    private const string DefaultPrimaryColor = "#1A4D4DFF";
    private const string DefaultSecondaryColor = "#103456FF";
    
    public Guid Id { get;  private set; }
    public Guid TenantId { get;  private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string LogoUrl { get; private set; } = string.Empty;
    public bool IsMaintenance { get; private set; }
    public string PrimaryColor { get; private set; } = DefaultPrimaryColor;
    public string SecondaryColor { get; private set; } = DefaultSecondaryColor;
    public Languages DefaultLanguage { get; private set; } = Languages.Fr;

    /// <summary>ISO 4217 code of every amount typed in this institution — see <see cref="InstitutionCurrencies"/>.</summary>
    public string Currency { get; private set; } = InstitutionCurrencies.Default;

    // ── VAT and discounts (quote lines, lot 1, 2026-11-10, product owner) ──────
    //
    // Set by the institution, never coded: within the UEMOA the standard rate lies between 15 % and
    // 20 % and each country picks its own. The defaults are Côte d'Ivoire's (18 % and 9 %), and they
    // are only a starting point for the administrator to confirm.

    public const decimal DefaultVatStandardRate = 18m;
    public const decimal DefaultVatReducedRate = 9m;

    /// <summary>Standard VAT rate, in percent.</summary>
    public decimal VatStandardRate { get; private set; } = DefaultVatStandardRate;

    /// <summary>Reduced VAT rate, in percent.</summary>
    public decimal VatReducedRate { get; private set; } = DefaultVatReducedRate;

    /// <summary>
    /// The highest discount an agent may grant on a quote without a manager's approval, in percent.
    /// Null = no ceiling set yet: no discount needs an approval until the institution sets one.
    /// </summary>
    public decimal? MaxDiscountPercent { get; private set; }

    // ── Legal identity, printed on quotes (quote lines, lot 2, 2026-11-10) ──────
    //
    // All optional and free text: the code imposes none of a quote's mandatory mentions, whose
    // exact list an accountant has to confirm (docs/devis-en-lignes.md § 6). The institution fills
    // what applies to it; the quote prints what is filled.

    public const int MaxLegalNameLength = 150;
    public const int MaxLegalFormLength = 60;
    public const int MaxHeadOfficeAddressLength = 300;
    public const int MaxLegalPhoneLength = 30;
    public const int MaxLegalEmailLength = 150;
    public const int MaxWebsiteLength = 200;
    public const int MaxRccmLength = 60;
    public const int MaxTaxpayerNumberLength = 30;
    public const int MaxTaxRegimeLength = 60;
    public const int MaxQuoteFooterLength = 1000;

    /// <summary>Raison sociale. Blank = the quote prints <see cref="Name"/>.</summary>
    public string? LegalName { get; private set; }
    /// <summary>SA, SARL, SAS, coopérative…</summary>
    public string? LegalForm { get; private set; }
    /// <summary>Head office, several lines (street, BP, city, country).</summary>
    public string? HeadOfficeAddress { get; private set; }
    public string? LegalPhone { get; private set; }
    public string? LegalEmail { get; private set; }
    public string? Website { get; private set; }
    /// <summary>Registre du commerce et du crédit mobilier.</summary>
    public string? Rccm { get; private set; }
    /// <summary>N° de compte contribuable (NCC in Côte d'Ivoire).</summary>
    public string? TaxpayerNumber { get; private set; }
    /// <summary>Régime d'imposition (réel normal, réel simplifié…), as the institution words it.</summary>
    public string? TaxRegime { get; private set; }
    /// <summary>Free text at the foot of every quote (capital, bank details, mentions…).</summary>
    public string? QuoteFooter { get; private set; }

    // ── Quote settings (lot 2) ──────────────────────────────────────────────────

    public const string DefaultQuotePrefix = "DEV";
    public const int DefaultQuoteValidityDays = 30;
    public const int MaxQuoteValidityDays = 365;
    public const int MaxQuotePrefixLength = 10;
    public const int MaxPaymentTermsLength = 500;

    /// <summary>Start of a quote number: <c>DEV</c> gives DEV-2026-000001. Letters, digits and dashes.</summary>
    public string QuotePrefix { get; private set; } = DefaultQuotePrefix;
    /// <summary>How long a quote stands, by default, in days.</summary>
    public int QuoteValidityDays { get; private set; } = DefaultQuoteValidityDays;
    /// <summary>Default payment terms printed on a quote (« 50 % à la commande, solde à la livraison »).</summary>
    public string? QuotePaymentTerms { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private CompanyInfo() { }

    public static CompanyInfo Create(Guid tenantId, string name, string description)
    {
        return new CompanyInfo
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Description = description,
            LogoUrl = "default.png",
        };
    }

    /// <summary>Pointe le logo vers l'image déposée, ou le retire. Les autres réglages ne bougent pas.</summary>
    public void SetLogoUrl(string logoUrl)
    {
        LogoUrl = logoUrl;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The institution's VAT rates and discount ceiling, set together. Rates between 0 and 100, the
    /// reduced one not above the standard one; the ceiling between 0 and 100, or null for none.
    /// </summary>
    /// <returns><c>VAT_RATES_INVALID</c> or <c>MAX_DISCOUNT_INVALID</c>.</returns>
    public Result SetPricingRules(decimal vatStandardRate, decimal vatReducedRate, decimal? maxDiscountPercent)
    {
        if (vatStandardRate is < 0 or > 100 || vatReducedRate is < 0 or > 100 || vatReducedRate > vatStandardRate)
            return Result.Fail("VAT_RATES_INVALID");
        if (maxDiscountPercent is < 0 or > 100)
            return Result.Fail("MAX_DISCOUNT_INVALID");

        VatStandardRate    = vatStandardRate;
        VatReducedRate     = vatReducedRate;
        MaxDiscountPercent = maxDiscountPercent;
        UpdatedAt          = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>
    /// The legal identity printed on quotes, replaced whole. Blank values clear; nothing is
    /// required.
    /// </summary>
    /// <returns><c>LEGAL_IDENTITY_TOO_LONG</c> naming nothing more: the validator names the field.</returns>
    public Result SetLegalIdentity(
        string? legalName, string? legalForm, string? headOfficeAddress, string? phone, string? email,
        string? website, string? rccm, string? taxpayerNumber, string? taxRegime, string? quoteFooter)
    {
        var values = new (string? Value, int Max)[]
        {
            (legalName, MaxLegalNameLength), (legalForm, MaxLegalFormLength),
            (headOfficeAddress, MaxHeadOfficeAddressLength), (phone, MaxLegalPhoneLength),
            (email, MaxLegalEmailLength), (website, MaxWebsiteLength), (rccm, MaxRccmLength),
            (taxpayerNumber, MaxTaxpayerNumberLength), (taxRegime, MaxTaxRegimeLength),
            (quoteFooter, MaxQuoteFooterLength),
        };
        if (values.Any(v => Clean(v.Value) is { } c && c.Length > v.Max)) return Result.Fail("LEGAL_IDENTITY_TOO_LONG");

        LegalName         = Clean(legalName);
        LegalForm         = Clean(legalForm);
        HeadOfficeAddress = Clean(headOfficeAddress);
        LegalPhone        = Clean(phone);
        LegalEmail        = Clean(email);
        Website           = Clean(website);
        Rccm              = Clean(rccm);
        TaxpayerNumber    = Clean(taxpayerNumber);
        TaxRegime         = Clean(taxRegime);
        QuoteFooter       = Clean(quoteFooter);
        UpdatedAt         = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>Prefix, default validity and default payment terms of the institution's quotes.</summary>
    /// <returns><c>QUOTE_PREFIX_INVALID</c>, <c>QUOTE_VALIDITY_INVALID</c> or <c>QUOTE_PAYMENT_TERMS_TOO_LONG</c>.</returns>
    public Result SetQuoteSettings(string? prefix, int validityDays, string? paymentTerms)
    {
        var p = NormalizeQuotePrefix(prefix);
        if (p is null) return Result.Fail("QUOTE_PREFIX_INVALID");
        if (validityDays is < 1 or > MaxQuoteValidityDays) return Result.Fail("QUOTE_VALIDITY_INVALID");
        var terms = Clean(paymentTerms);
        if (terms is { Length: > MaxPaymentTermsLength }) return Result.Fail("QUOTE_PAYMENT_TERMS_TOO_LONG");

        QuotePrefix       = p;
        QuoteValidityDays = validityDays;
        QuotePaymentTerms = terms;
        UpdatedAt         = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>Upper case, trimmed; null when blank, too long, or not letters, digits and dashes.</summary>
    public static string? NormalizeQuotePrefix(string? prefix)
    {
        var p = prefix?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(p) || p.Length > MaxQuotePrefixLength) return null;
        if (!p.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')) return null;
        if (p.StartsWith('-') || p.EndsWith('-')) return null;
        return p;
    }

    /// <summary>« DEV-2026-000001 »: prefix, year of issue, rank in that year on six digits.</summary>
    public static string FormatQuoteNumber(string prefix, int year, long rank) => $"{prefix}-{year}-{rank:D6}";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Update(
        string name,
        string description,
        string logoUrl,
        string primaryColor,
        string secondaryColor,
        Languages defaultLanguage,
        string? currency = null)
    {
        Name = name;
        Description = description;
        LogoUrl = logoUrl;
        PrimaryColor = primaryColor;
        SecondaryColor = secondaryColor;
        DefaultLanguage = defaultLanguage;
        // Absent from an older client's request: the institution keeps its currency.
        if (currency is not null) Currency = currency.Trim().ToUpperInvariant();
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}