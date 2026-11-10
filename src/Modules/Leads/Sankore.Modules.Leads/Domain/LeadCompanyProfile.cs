namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Headcount of a corporate prospect, by bracket (key accounts lot 1, 2026-11-09, product owner).
/// A bracket rather than a figure: the agent on the phone rarely knows the exact number, and the
/// score (lot 2) only needs the order of magnitude.
/// </summary>
public enum LeadCompanySize
{
    /// <summary>1 to 9 people.</summary>
    Micro,
    /// <summary>10 to 49 people.</summary>
    Small,
    /// <summary>50 to 249 people.</summary>
    Medium,
    /// <summary>250 people and more.</summary>
    Large,
}

/// <summary>
/// The activity sectors a corporate prospect may be filed under — a NEUTRAL list of the socle, the
/// same for every institution (key accounts lot 1). The client's own sector
/// (<c>Client.ActivitySectorCode</c>) follows the core banking referential instead, so the
/// conversion carries the LABEL into the client's activity detail, never the code.
/// </summary>
public static class LeadActivitySectors
{
    public static readonly IReadOnlyList<(string Code, string Label)> All =
    [
        ("WholesaleTrade",    "Commerce de gros et distribution"),
        ("RetailTrade",       "Commerce de détail"),
        ("Construction",      "BTP et construction"),
        ("Manufacturing",     "Industrie et transformation"),
        ("Agriculture",       "Agriculture, élevage et pêche"),
        ("EnergyMining",      "Énergie, mines et pétrole"),
        ("TransportLogistics","Transport et logistique"),
        ("HospitalityTourism","Hôtellerie, restauration et tourisme"),
        ("Health",            "Santé et pharmacie"),
        ("Education",         "Éducation et formation"),
        ("PublicSector",      "Administration et collectivités"),
        ("NonProfit",         "ONG, associations et organisations internationales"),
        ("Technology",        "Informatique et télécommunications"),
        ("BusinessServices",  "Services aux entreprises"),
        ("RealEstate",        "Immobilier"),
        ("FinancialServices", "Banques, assurances et services financiers"),
        ("MediaEvents",       "Médias, communication et événementiel"),
        ("Other",             "Autre"),
    ];

    /// <summary>The code as stored, from a code or a label in any case; null when unknown.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        foreach (var (code, label) in All)
            if (string.Equals(code, v, StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, v, StringComparison.OrdinalIgnoreCase))
                return code;
        return null;
    }

    public static string? LabelOf(string? code) =>
        code is null ? null : All.FirstOrDefault(s => s.Code == code).Label;
}

/// <summary>
/// Where a company prospect stands against the institution's key-account thresholds (key accounts
/// lot 4): read from its yearly purchases, never stored — a change of thresholds re-ranks every lead
/// at once. A person is always <see cref="None"/>.
/// </summary>
public enum LeadPotentialTier
{
    None,
    /// <summary>Yearly purchases at or above the « bon client » threshold.</summary>
    GoodCustomer,
    /// <summary>Yearly purchases at or above the « grand compte » threshold.</summary>
    KeyAccount,
}

public static class LeadCompanySizes
{
    public static string Label(LeadCompanySize size) => size switch
    {
        LeadCompanySize.Micro  => "1 à 9 personnes",
        LeadCompanySize.Small  => "10 à 49 personnes",
        LeadCompanySize.Medium => "50 à 249 personnes",
        LeadCompanySize.Large  => "250 personnes et plus",
        _ => size.ToString(),
    };

    /// <summary>
    /// A bracket from its name or label in any case, or from a headcount (« 35 » → Small), which is
    /// what an import column or a web form most often holds. Null when unreadable.
    /// </summary>
    public static LeadCompanySize? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (Enum.TryParse<LeadCompanySize>(v, ignoreCase: true, out var named) && Enum.IsDefined(named)
            && !int.TryParse(v, out _))
            return named;
        foreach (var size in Enum.GetValues<LeadCompanySize>())
            if (string.Equals(Label(size), v, StringComparison.OrdinalIgnoreCase))
                return size;
        if (int.TryParse(v.Replace(" ", "").Replace(" ", ""), out var headcount) && headcount > 0)
            return headcount switch
            {
                < 10  => LeadCompanySize.Micro,
                < 50  => LeadCompanySize.Small,
                < 250 => LeadCompanySize.Medium,
                _     => LeadCompanySize.Large,
            };
        return null;
    }
}
