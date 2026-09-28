namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// One tenant-level setting of the Customers module, as seeded on tenant creation.
/// <paramref name="ValueType"/> is one of <c>"string"</c>, <c>"int"</c>, <c>"bool"</c>,
/// <c>"decimal"</c> or <c>"json"</c> and drives both parsing and the admin UI widget.
/// </summary>
public sealed record CustomerSettingDefault(string Key, string Value, string ValueType, string Description);

/// <summary>
/// Keys of the M01 tenant settings plus their factory defaults.
/// Never hard-code a key string anywhere else — always reference a constant from here,
/// otherwise a typo silently resolves to <see cref="CustomerErrors.SettingUnknown"/>.
/// </summary>
public static class CustomerSettingKeys
{
    public const string ClientNumberFormat = "client-number-format";
    public const string MinimumAge = "minimum-age";
    public const string BeneficialOwnerThreshold = "beneficial-owner-threshold";
    public const string DuplicateScoreThreshold = "duplicate-score-threshold";
    public const string RevealLimitPerHour = "reveal-limit-per-hour";
    public const string AdvisorFromConvertingAgent = "advisor-from-converting-agent";
    public const string RetentionYears = "retention-years";
    public const string ExportLinkTtlMinutes = "export-link-ttl-minutes";
    public const string GroupMinSizeSolidarity = "group-min-size-solidarity";
    public const string GroupMaxSizeSolidarity = "group-max-size-solidarity";
    public const string GroupMinSizeTontine = "group-min-size-tontine";
    public const string GroupMaxSizeTontine = "group-max-size-tontine";
    public const string GroupMinSizeVsla = "group-min-size-vsla";
    public const string GroupMaxSizeVsla = "group-max-size-vsla";
    public const string SolidaritySingleGroupRule = "solidarity-single-group-rule";
    public const string SegmentRulesJson = "segment-rules-json";
    public const string LoyaltyWeightsJson = "loyalty-weights-json";
    public const string KycStubEnabled = "kyc-stub-enabled";

    // ── Value types ─────────────────────────────────────────────────────────
    public const string TypeString = "string";
    public const string TypeInt = "int";
    public const string TypeBool = "bool";
    public const string TypeDecimal = "decimal";
    public const string TypeJson = "json";

    /// <summary>Factory defaults, seeded once per tenant. Order is the admin-screen order.</summary>
    public static readonly IReadOnlyList<CustomerSettingDefault> Defaults =
    [
        new(ClientNumberFormat, "{AgencyCode}-{YYYY}-{Seq:6}", TypeString,
            "Client number pattern. Supported tokens: {AgencyCode}, {YYYY}, {Seq:n}."),
        new(MinimumAge, "18", TypeInt,
            "Minimum age (years) required to register an individual client."),
        new(BeneficialOwnerThreshold, "25", TypeDecimal,
            "Ownership percentage above which a shareholder must be declared as a beneficial owner."),
        new(DuplicateScoreThreshold, "70", TypeInt,
            "Match score from which two clients are flagged as duplicate candidates."),
        new(RevealLimitPerHour, "20", TypeInt,
            "Maximum number of sensitive-field reveals allowed per user per rolling hour."),
        new(AdvisorFromConvertingAgent, "true", TypeBool,
            "When a lead is converted, assign the converting agent as the client advisor."),
        new(RetentionYears, "10", TypeInt,
            "Years an archived client is retained before anonymization becomes possible."),
        new(ExportLinkTtlMinutes, "60", TypeInt,
            "Lifetime (minutes) of a client export download link."),
        new(GroupMinSizeSolidarity, "3", TypeInt, "Minimum members of a solidarity group."),
        new(GroupMaxSizeSolidarity, "30", TypeInt, "Maximum members of a solidarity group."),
        new(GroupMinSizeTontine, "5", TypeInt, "Minimum members of a tontine."),
        new(GroupMaxSizeTontine, "50", TypeInt, "Maximum members of a tontine."),
        new(GroupMinSizeVsla, "15", TypeInt, "Minimum members of a VSLA."),
        new(GroupMaxSizeVsla, "30", TypeInt, "Maximum members of a VSLA."),
        new(SolidaritySingleGroupRule, "true", TypeBool,
            "Forbid a client from belonging to more than one active solidarity group."),
        new(SegmentRulesJson, "[]", TypeJson,
            "Ordered segmentation rules evaluated by the nightly segmentation job."),
        new(LoyaltyWeightsJson, """{"tenure":30,"regularity":30,"volume":20,"products":20}""", TypeJson,
            "Weights (summing to 100) of the loyalty score components."),
        new(KycStubEnabled, "false", TypeBool,
            "Auto-approve KYC locally instead of waiting for module M02 (non-production only)."),
    ];

    /// <summary>
    /// The same defaults keyed by setting key. <see cref="Defaults"/> stays the ordered
    /// list because the admin screen renders it in that order; this is the lookup the
    /// settings service and the seeder need. Both views share one declaration, so a new
    /// setting cannot appear in one and be missing from the other.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, CustomerSettingDefault> DefaultsByKey =
        Defaults.ToDictionary(d => d.Key, StringComparer.Ordinal);
}

/// <summary>
/// Legal forms seeded for a new tenant (West-African OHADA set).
/// The list is editable per tenant afterwards through <see cref="LegalForm"/>.
/// </summary>
public static class DefaultLegalForms
{
    public static readonly IReadOnlyList<(string Code, string Label)> All =
    [
        ("SARL", "Société à responsabilité limitée"),
        ("SA", "Société anonyme"),
        ("SAS", "Société par actions simplifiée"),
        ("SCOOPS", "Société coopérative simplifiée"),
        ("GIE", "Groupement d'intérêt économique"),
        ("Association", "Association"),
        ("SNC", "Société en nom collectif"),
        ("SCI", "Société civile immobilière"),
        ("ONG", "Organisation non gouvernementale"),
    ];
}
