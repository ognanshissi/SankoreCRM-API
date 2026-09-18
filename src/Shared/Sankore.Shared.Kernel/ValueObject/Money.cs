namespace Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Immutable monetary amount with currency. Uses ISO 4217 currency codes.
/// EF Core maps it as an owned type — see each module's EntityTypeConfiguration.
/// </summary>
public record Money
{
    public decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code, e.g. "XOF", "USD", "EUR".</summary>
    public string Currency { get; init; }

    private Money() { Currency = string.Empty; } // EF Core

    public Money(decimal amount, string currency)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency code is required.", nameof(currency));

        Amount   = amount;
        Currency = currency.Trim().ToUpperInvariant();
    }

    public Money Add(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot add {Currency} and {other.Currency}.");
        return new Money(Amount + other.Amount, Currency);
    }

    public override string ToString() => $"{Amount:N2} {Currency}";
}
