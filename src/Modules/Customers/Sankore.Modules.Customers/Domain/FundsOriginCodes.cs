namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// Where a client's assets and funds come from — a CLOSED list, for the LBC/FT requirement to
/// « établir l'origine du patrimoine et des fonds » of a politically exposed person.
///
/// <para>
/// <b>A closed list and not free text</b>, for the same reason <c>PersonFieldCode</c> is one in
/// M13: free text is a field that reads as collected and cannot be reported on. A regulator asks
/// how many of an institution's exposed clients declared an inheritance, and a column of
/// sentences answers nothing. The narrative is kept too, encrypted, next to the code — the code is
/// what is countable, the sentence is what is true.
/// </para>
///
/// <para>
/// Validated at the write, so a typo is a 400 rather than a value nothing can group by. The list
/// is deliberately short and ordinary: a longer one is a form nobody fills honestly.
/// </para>
/// </summary>
public static class FundsOriginCodes
{
    /// <summary>Salary, wages, pension.</summary>
    public const string Employment = "EMPLOYMENT";

    /// <summary>Profit of a trade or a profession — the commonest answer in this market.</summary>
    public const string BusinessIncome = "BUSINESS_INCOME";

    /// <summary>Farming or livestock revenue, which an IMF portfolio is full of.</summary>
    public const string Agriculture = "AGRICULTURE";

    public const string Inheritance = "INHERITANCE";

    /// <summary>Sale of property or of a business.</summary>
    public const string AssetSale = "ASSET_SALE";

    /// <summary>Investment or rental income.</summary>
    public const string Investment = "INVESTMENT";

    /// <summary>Money sent from abroad — a major flow in the UEMOA region.</summary>
    public const string Remittances = "REMITTANCES";

    /// <summary>A grant, a loan, or support from a programme.</summary>
    public const string GrantOrLoan = "GRANT_OR_LOAN";

    /// <summary>
    /// Something the list does not cover. Accepted, because a list that forces a wrong answer is
    /// worse than one that admits its limits — and the encrypted narrative beside it is then the
    /// record that matters.
    /// </summary>
    public const string Other = "OTHER";

    public static readonly IReadOnlyList<string> All =
    [
        Employment, BusinessIncome, Agriculture, Inheritance,
        AssetSale, Investment, Remittances, GrantOrLoan, Other,
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="code"/> is part of the list. Case-insensitive, and <c>true</c> for
    /// null or blank: "not recorded yet" is not an invalid code, and the field is optional until a
    /// vigilance level makes it required.
    /// </summary>
    public static bool IsValid(string? code)
        => string.IsNullOrWhiteSpace(code) || Known.Contains(code.Trim());
}
