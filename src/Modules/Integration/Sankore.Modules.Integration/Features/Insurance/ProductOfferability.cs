namespace Sankore.Modules.Integration.Features.Insurance;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// ASS-03's fourth criterion — « un produit dont la connexion est inactive n'est pas
/// proposable » — in one place.
///
/// <para>
/// <b>Derived, never stored.</b> A column on <c>ins_product</c> would be a cached answer to a
/// question about ANOTHER row: the moment an administrator deactivates the ORASS connection, every
/// product of that insurer must stop being offerable, and nothing would recompute the flag. The
/// same applies to the validity window, which goes stale by the passage of time alone, and to the
/// insurer-pricing capability, which is read from the adapter and changes with the version
/// installed at the IMF (INT-31 computes it from the connection's settings). Three independent
/// ways for a stored flag to be quietly wrong, each of them in the direction that offers a product
/// which cannot be sold.
/// </para>
///
/// <para>
/// One function, used by the catalogue read AND — in the subscription slice of ASS-04 — by the
/// write path, so "offerable" cannot come to mean two things. The reasons are codes rather than
/// sentences: the agent screen translates them, and a refusal the agent cannot explain sends the
/// customer away without knowing why (the same argument <c>InsuranceEligibility</c> makes).
/// </para>
/// </summary>
internal static class ProductOfferability
{
    /// <summary>The tenant withdrew the product from its own catalogue.</summary>
    internal const string ProductInactive = "PRODUCT_INACTIVE";

    /// <summary>The insurer's connection is deactivated — the criterion's own case.</summary>
    internal const string ConnectionInactive = "CONNECTION_INACTIVE";

    /// <summary>
    /// The connection is not an insurance one. Belt and braces: the create and update handlers
    /// refuse it, so this can only be reached by a row written before that check, or by hand.
    /// </summary>
    internal const string ConnectionWrongFamily = "CONNECTION_WRONG_FAMILY";

    /// <summary>The product's validity window has not opened yet.</summary>
    internal const string NotYetEffective = "NOT_YET_EFFECTIVE";

    /// <summary>The validity window has closed.</summary>
    internal const string NoLongerEffective = "NO_LONGER_EFFECTIVE";

    /// <summary>
    /// The product is priced by the insurer and the connection's adapter does not declare
    /// <c>IntegrationCapability.PriceProduct</c> — so there is no premium to debit. A reason and
    /// not a hidden row: an administrator seeing this knows the catalogue entry is right and the
    /// connection is the problem.
    /// </summary>
    internal const string InsurerPricingUnavailable = "INSURER_PRICING_UNAVAILABLE";

    /// <summary>
    /// The CRM catalogue entry this product realises has been retired in M12 — the institution
    /// stopped selling the product.
    ///
    /// <para>
    /// The same kind of fact as <see cref="ConnectionInactive"/>, and here for the same reason: it
    /// is a statement about ANOTHER row, which changes without this one being touched. An
    /// administrator retiring a product in the catalogue expects it to stop being sold at every
    /// insurer that distributes it, in that instant; a column here would be correct until then and
    /// wrong afterwards. Existing policies keep running — a withdrawal stops new subscriptions, it
    /// does not cancel contracts.
    /// </para>
    ///
    /// <para>
    /// Only M12's activity flag is read, deliberately NOT its validity window: two date windows on
    /// one product is a configuration nobody gets right twice, and ASS-03 puts the window on the
    /// insurance product, which is where it stays.
    /// </para>
    /// </summary>
    internal const string CrmProductWithdrawn = "CRM_PRODUCT_WITHDRAWN";

    /// <summary>
    /// The product names a catalogue entry that resolves to nothing. Belt and braces, like
    /// <see cref="ConnectionWrongFamily"/>: the create and update handlers refuse an unresolvable
    /// code, and M12 retires products rather than deleting them, so reaching this means a row
    /// written before the check existed or by hand. Reported rather than ignored — an insurance
    /// product whose CRM identity is gone is invisible to every product-by-code report, and
    /// selling it would compound that silently.
    /// </summary>
    internal const string CrmProductUnknown = "CRM_PRODUCT_UNKNOWN";

    /// <summary>
    /// Every reason this product cannot be offered, or an empty list. All of them, not the first:
    /// an administrator fixing one and finding the next is how a configuration screen wastes an
    /// afternoon.
    /// </summary>
    internal static IReadOnlyList<string> Reasons(
        bool productIsActive,
        bool connectionIsActive,
        bool connectionIsInsuranceFamily,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        ProductPricingMode pricingMode,
        bool insurerPricingAvailable,
        CrmCatalogueStatus crmCatalogue,
        DateOnly today)
    {
        var reasons = new List<string>(3);

        if (!productIsActive) reasons.Add(ProductInactive);
        if (!connectionIsActive) reasons.Add(ConnectionInactive);
        if (!connectionIsInsuranceFamily) reasons.Add(ConnectionWrongFamily);
        if (effectiveFrom > today) reasons.Add(NotYetEffective);
        if (effectiveTo is { } end && end < today) reasons.Add(NoLongerEffective);

        if (pricingMode == ProductPricingMode.InsurerComputed && !insurerPricingAvailable)
            reasons.Add(InsurerPricingUnavailable);

        // NotLinked adds nothing: a product that names no catalogue entry is not thereby
        // unsellable, and every product configured before the column existed is in that state.
        if (crmCatalogue == CrmCatalogueStatus.Withdrawn) reasons.Add(CrmProductWithdrawn);
        if (crmCatalogue == CrmCatalogueStatus.Unknown) reasons.Add(CrmProductUnknown);

        return reasons;
    }

    /// <summary>
    /// The half of the verdict the DATABASE can decide, as a predicate the list endpoint pushes
    /// into SQL for its <c>offerableOnly</c> filter and its paging.
    ///
    /// <para>
    /// The insurer-pricing reason is deliberately absent: deciding it needs an adapter per row,
    /// which cannot happen inside a query. The two CRM-catalogue reasons are absent for the
    /// stronger version of the same objection — the catalogue lives in another module's schema,
    /// reachable only through <c>IAdministrationModule</c>, so no SQL predicate of THIS context
    /// can join to it. A product excluded from this predicate is never offerable; one that passes
    /// it may still carry <see cref="InsurerPricingUnavailable"/>,
    /// <see cref="CrmProductWithdrawn"/> or <see cref="CrmProductUnknown"/>, and the DTO's own
    /// <c>isOfferable</c> — computed from <see cref="Reasons"/> — is the full verdict. Callers
    /// branch on that, never on having asked for the filter.
    /// </para>
    /// </summary>
    internal static bool IsOfferableInDatabase(
        bool productIsActive,
        bool connectionIsActive,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        DateOnly today)
        => productIsActive
           && connectionIsActive
           && effectiveFrom <= today
           && (effectiveTo == null || effectiveTo >= today);
}
