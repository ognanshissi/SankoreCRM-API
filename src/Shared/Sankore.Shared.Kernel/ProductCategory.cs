namespace Sankore.Shared.Kernel;

/// <summary>
/// The family a catalogue product belongs to. Persisted as a STRING everywhere
/// (<c>HasConversion&lt;string&gt;</c>), so appending a value is safe and reordering is harmless.
///
/// <para>
/// BNF-B-06 lists <c>CurrentAccount, Savings, Credit, Insurance, Other</c>. Four of those already
/// had a home here — <c>Credit</c> under the name <see cref="Loan"/> — so only
/// <see cref="CurrentAccount"/> was added.
/// </para>
///
/// <para>
/// <b>There is deliberately no <c>Other</c>.</b> A category is what resolves a product's
/// beneficiary policy, its required KYC tier and its integration system; a product filed under
/// "Other" has none of those answerable, and the failure would surface as a conversion refused for
/// reasons nobody can act on. A product that fits no value here needs a new value, which is one
/// line and a migration-free deploy.
/// </para>
/// </summary>
public enum ProductCategory
{
    Loan,
    Savings,
    Insurance,
    HealthInsurance,
    ForecastInsurance,
    Tontine,
    GroupCredit,
    Agriculture,

    /// <summary>A sight/transaction account (BNF-B-06). Appended, so no stored value moves.</summary>
    CurrentAccount,

    /// <summary>
    /// « Produit standard » — the socle's category, belonging to no paid module, so a tenant
    /// running on the socle alone still has a catalogue. It is not the <c>Other</c> the remark
    /// above refuses: every question a category answers has an explicit answer here — no
    /// household and no designation (<c>BeneficiaryPolicy.None</c>), the tenant's default KYC
    /// tier, no integration system, the generic qualification template, never the CIMA card
    /// guard. Appended, so no stored value moves.
    /// </summary>
    Standard,
}
/// <summary>
/// Which product categories are INSURANCE, in one place.
///
/// <para>
/// There are three, not one — <see cref="ProductCategory.Insurance"/> alongside
/// <see cref="ProductCategory.HealthInsurance"/> and
/// <see cref="ProductCategory.ForecastInsurance"/> (<i>prévoyance</i>) — and the list had already
/// been written out three times: in M-Integration's CRM catalogue probe, in its product-routing
/// report, and in M13's health-questionnaire rule. A fourth copy was about to be written for the
/// CIMA professional-card guard, which is what moved it here.
/// </para>
///
/// <para>
/// It matters because every copy is a place where a health cover can be forgotten. A guard that
/// checks only <c>Insurance</c> lets an agent with no professional card distribute a health
/// policy — the licence requirement of CIMA article 510 does not care how precisely the product
/// was described.
/// </para>
/// </summary>
public static class ProductCategories
{
    /// <summary>The categories an insurance distribution agreement may realise.</summary>
    public static readonly IReadOnlyList<ProductCategory> Insurance =
    [
        ProductCategory.Insurance,
        ProductCategory.HealthInsurance,
        ProductCategory.ForecastInsurance,
    ];

    /// <summary>The same list as the strings a DTO and a settings row carry.</summary>
    public static readonly IReadOnlyList<string> InsuranceNames =
        [.. Insurance.Select(c => c.ToString())];

    /// <summary>
    /// Whether <paramref name="category"/> is an insurance category. Accepts the enum's NAME
    /// because that is the shape it travels in — <c>BeneficiaryPolicyDto.Category</c>,
    /// <c>ProductSummary.Category</c> and <c>ProductHolding.ProductCategory</c> are all strings.
    /// Case-insensitive, and <c>false</c> for null, blank or an unrecognised value: an
    /// unresolvable category is not an insurance category, and guessing otherwise would gate
    /// products nobody meant to gate.
    /// </summary>
    public static bool IsInsurance(string? category)
        => !string.IsNullOrWhiteSpace(category)
        && Enum.TryParse<ProductCategory>(category, ignoreCase: true, out var parsed)
        && Insurance.Contains(parsed);

    /// <summary>The typed overload.</summary>
    public static bool IsInsurance(ProductCategory category) => Insurance.Contains(category);

    /// <summary>
    /// The categories of the « Banking - Microfinance » module: banking products and the
    /// microfinance ones (tontine, group credit, agriculture), by the product owner's decision.
    /// </summary>
    public static readonly IReadOnlyList<ProductCategory> BankingMicrofinance =
    [
        ProductCategory.Loan,
        ProductCategory.Savings,
        ProductCategory.CurrentAccount,
        ProductCategory.Tontine,
        ProductCategory.GroupCredit,
        ProductCategory.Agriculture,
    ];

    /// <summary>
    /// The paid module a category belongs to (<see cref="TenantModules"/>), or null for
    /// <see cref="ProductCategory.Standard"/>, which is socle. Throws for a category attached to
    /// no module: a value appended to the enum must be placed in a module or declared socle, and
    /// <c>ProductCategoryModuleTests</c> walks every value so the omission fails a test, not a
    /// request.
    /// </summary>
    public static string? ModuleOf(ProductCategory category) => category switch
    {
        ProductCategory.Standard => null,
        _ when Insurance.Contains(category) => TenantModules.Insurance,
        _ when BankingMicrofinance.Contains(category) => TenantModules.BankingMicrofinance,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category,
            "This category belongs to no module and is not the socle's Standard."),
    };

    /// <summary>
    /// Whether a tenant running <paramref name="activeModules"/> may create something in
    /// <paramref name="category"/>. Standard always; any other only with its module.
    /// </summary>
    public static bool IsOpenWith(ProductCategory category, IReadOnlyCollection<string> activeModules)
        => ModuleOf(category) is not { } module || activeModules.Contains(module);
}
