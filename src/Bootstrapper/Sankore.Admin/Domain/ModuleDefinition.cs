namespace Sankore.Admin.Domain;

/// <summary>
/// A paid module as the platform sells it: its code (<see cref="Sankore.Shared.Kernel.TenantModules"/>),
/// the wording a tenant administrator reads before asking for it, and its current price.
///
/// <para>
/// The price here is the CURRENT one. What a tenant agreed to pay is copied onto its own
/// <see cref="TenantModuleSubscription"/> at request time, so a later price change never rewrites
/// what was accepted — that copy is the proof if an invoice is disputed.
/// </para>
///
/// <para>
/// <see cref="IsAvailable"/> ships <c>false</c>: the migration seeds the two modules at price 0,
/// and a module nobody has priced yet must not be requestable — a tenant would accept « 0 XOF ».
/// </para>
/// </summary>
public sealed class ModuleDefinition
{
    public const int CodeMaxLength = 50;
    public const int LabelMaxLength = 100;
    public const int DescriptionMaxLength = 1000;
    public const int CurrencyLength = 3;

    public string Code { get; private set; } = null!;
    public string Label { get; private set; } = null!;
    public string Description { get; private set; } = string.Empty;
    public decimal MonthlyPrice { get; private set; }
    public string Currency { get; private set; } = "XOF";
    public bool IsAvailable { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ModuleDefinition() { }

    public static ModuleDefinition Create(string code, string label, string description, DateTimeOffset now) => new()
    {
        Code = code,
        Label = label,
        Description = description,
        MonthlyPrice = 0m,
        Currency = "XOF",
        IsAvailable = false,
        UpdatedAt = now,
    };

    public void Update(string label, string description, decimal monthlyPrice, string currency, bool isAvailable,
        DateTimeOffset now)
    {
        Label = label.Trim();
        Description = description.Trim();
        MonthlyPrice = monthlyPrice;
        Currency = currency.Trim().ToUpperInvariant();
        IsAvailable = isAvailable;
        UpdatedAt = now;
    }

    /// <summary>
    /// True when a tenant's accepted figures are still the current ones. Compared exactly: the
    /// tenant accepted the amount it was shown, and anything else is a price it never saw.
    /// </summary>
    public bool MatchesPrice(decimal acceptedMonthlyPrice, string acceptedCurrency) =>
        MonthlyPrice == acceptedMonthlyPrice
        && string.Equals(Currency, acceptedCurrency?.Trim(), StringComparison.OrdinalIgnoreCase);
}
