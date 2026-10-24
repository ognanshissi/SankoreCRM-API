namespace Sankore.Shared.Kernel;

/// <summary>
/// The currencies an institution can keep its amounts in — one per tenant, chosen by its
/// administrator (company profile). Every amount a user types is in it: there is no currency
/// choice in the forms, so totals, forecasts and reports add up without conversion. Amounts
/// stored before a change keep the currency they were typed in.
/// </summary>
public static class InstitutionCurrencies
{
    /// <summary>The zone the platform first served, and the currency of a tenant that never chose.</summary>
    public const string Default = "XOF";

    public static readonly IReadOnlyList<string> Supported =
        ["XOF", "XAF", "GNF", "CDF", "MAD", "NGN", "GHS", "KES", "EUR", "USD"];

    public static bool IsSupported(string? code) =>
        code is not null && Supported.Contains(code.Trim().ToUpperInvariant());

    /// <summary>An amount written in a currency that is neither the institution's nor its own.</summary>
    public const string NotInstitution = "CURRENCY_NOT_INSTITUTION";

    /// <summary>
    /// The currency an amount being written is stored in.
    /// <list type="bullet">
    /// <item>None given: the institution's — the forms no longer offer a choice.</item>
    /// <item>The institution's, or the one this amount <paramref name="stored"/> already has: kept.
    /// The second case is a lead typed before the administrator changed the currency, edited
    /// since: refusing its own currency would block every edit of it, and relabelling it would
    /// change its value without anybody typing it.</item>
    /// <item>Anything else: <see cref="NotInstitution"/>.</item>
    /// </list>
    /// A blank <paramref name="institution"/> (a tenant whose profile is not readable yet, a test
    /// double) counts as <see cref="Default"/>.
    /// </summary>
    public static Result<string> Resolve(string? requested, string? institution, string? stored = null)
    {
        var own = string.IsNullOrWhiteSpace(institution) ? Default : institution.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(requested)) return Result.Ok(own);

        var code = requested.Trim().ToUpperInvariant();
        if (code == own) return Result.Ok(code);
        if (!string.IsNullOrWhiteSpace(stored) && code == stored.Trim().ToUpperInvariant()) return Result.Ok(code);
        return Result.Fail<string>(NotInstitution);
    }
}
