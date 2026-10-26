namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// The product-shaped terms of a holding (BNF-B-14) — stored as a typed <c>jsonb</c> column on
/// <see cref="ProductHolding"/>.
///
/// <para>
/// <b>A FLAT record and not a polymorphic hierarchy, deliberately.</b> One record with optional
/// fields per category, rather than <c>SavingsSpecifics</c> / <c>InsuranceSpecifics</c> / … behind
/// a <c>$kind</c> discriminator. The module has already paid for the polymorphic version once:
/// `$kind` is invisible in the OpenAPI document, so the generated Angular client can only emit a
/// body that comes back 422, and the fix is a Swashbuckle schema filter that somebody has to
/// remember exists (see M13's <c>SourceSettings</c>). Nothing here is dispatched on, nothing is
/// queried individually, and every consumer reads the record whole — so the discriminator would buy
/// nothing and cost a filter.
/// </para>
///
/// <para>
/// Which fields are MEANINGFUL depends on the product line's category, and that is not enforced
/// here: a holding whose line is a current account simply carries nulls. Validating the shape per
/// category belongs to the slice that knows the category, not to a value object that would have to
/// be told.
/// </para>
/// </summary>
/// <param name="InsuredCapital">Amount covered. Insurance lines.</param>
/// <param name="Premium">What the holder pays per <paramref name="Periodicity"/>.</param>
/// <param name="Periodicity">One of <see cref="Periodicities"/>, or null.</param>
/// <param name="DurationMonths">Term of the contract. Null = open-ended.</param>
/// <param name="AnnualRatePercent">Interest or yield rate. Savings, tontine and loan lines.</param>
/// <param name="InitialDeposit">Opening amount. Savings and tontine lines.</param>
/// <param name="Currency">ISO 4217. Applies to every amount in this record, which is why it is one field.</param>
/// <param name="EffectiveFrom">When cover or the account starts. Not the signature date.</param>
/// <param name="Notes">Free text an agent typed. Never a place for a value a column should hold.</param>
public sealed record HoldingSpecifics(
    decimal? InsuredCapital = null,
    decimal? Premium = null,
    string? Periodicity = null,
    int? DurationMonths = null,
    decimal? AnnualRatePercent = null,
    decimal? InitialDeposit = null,
    string? Currency = null,
    DateOnly? EffectiveFrom = null,
    string? Notes = null)
{
    /// <summary>What a holding carries until somebody fills the terms in. Never null on the entity.</summary>
    public static HoldingSpecifics None { get; } = new();

    /// <summary>
    /// Validates and normalises what a caller submitted.
    /// </summary>
    /// <returns>
    /// <c>HOLDING_SPECIFICS_INVALID_AMOUNT</c>, <c>HOLDING_SPECIFICS_INVALID_RATE</c> or
    /// <c>HOLDING_SPECIFICS_INVALID_PERIODICITY</c>.
    /// </returns>
    public static Result<HoldingSpecifics> Create(
        decimal? insuredCapital,
        decimal? premium,
        string? periodicity,
        int? durationMonths,
        decimal? annualRatePercent,
        decimal? initialDeposit,
        string? currency,
        DateOnly? effectiveFrom,
        string? notes)
    {
        if (insuredCapital is < 0 || premium is < 0 || initialDeposit is < 0 || durationMonths is < 0)
            return Result.Fail<HoldingSpecifics>("HOLDING_SPECIFICS_INVALID_AMOUNT");

        // An upper bound as well as a lower one: a rate typed as 1200 instead of 12.00 is the
        // mistake this catches, and it is the one that reaches a back office looking plausible.
        if (annualRatePercent is < 0 or > 100)
            return Result.Fail<HoldingSpecifics>("HOLDING_SPECIFICS_INVALID_RATE");

        // A closed list, stored in its canonical spelling. The field was free text: « Mensuelle »
        // was saved as typed, shown back raw, and meant nothing to the insurer that reads it.
        var periodicityValue = Blank(periodicity);
        if (periodicityValue is not null)
        {
            var known = Periodicities.FirstOrDefault(p => string.Equals(p, periodicityValue, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                return Result.Fail<HoldingSpecifics>("HOLDING_SPECIFICS_INVALID_PERIODICITY");
            periodicityValue = known;
        }

        return Result.Ok(new HoldingSpecifics(
            InsuredCapital:    insuredCapital,
            Premium:           premium,
            Periodicity:       periodicityValue,
            DurationMonths:    durationMonths,
            AnnualRatePercent: annualRatePercent,
            InitialDeposit:    initialDeposit,
            Currency:          currency is null ? null : Blank(currency)?.ToUpperInvariant(),
            EffectiveFrom:     effectiveFrom,
            Notes:             Blank(notes)));
    }

    /// <summary>
    /// The premium periodicities, spelled as Integration's <c>PremiumPeriodicity</c> — the insurer
    /// side reads this value, and this module cannot reference that contract.
    /// </summary>
    public static readonly IReadOnlyList<string> Periodicities = ["Single", "Monthly", "Quarterly", "SemiAnnual", "Annual"];

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
