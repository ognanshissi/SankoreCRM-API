namespace Sankore.Modules.Kyc.Domain;

/// <summary>
/// One tenant-level KYC parameter and its factory default. <paramref name="ValueType"/> is one of
/// <c>"string"</c>, <c>"int"</c>, <c>"bool"</c>, <c>"decimal"</c> — it drives both parsing and the
/// admin widget, exactly as <c>CustomerSettingDefault</c> does for M01.
/// </summary>
public sealed record KycSettingDefault(string Key, string Value, string ValueType, string Description);

/// <summary>
/// Keys of the M02 tenant settings plus their factory defaults.
///
/// They live in this module rather than in M12 — an explicit decision: M12 exposes no generic
/// settings store, and M01 already owns its own parameters the same way (<c>customer_settings</c>).
/// A KYC ceiling is a compliance rule of the KYC module, and the module that enforces it should own
/// it.
///
/// Never hard-code a key string elsewhere — reference a constant from here, or a typo resolves
/// silently to <see cref="KycErrors.SettingUnknown"/>.
/// </summary>
public static class KycSettingKeys
{
    // ── Simplified-tier ceilings (amounts in XOF) ───────────────────────────
    public const string SimplifiedMaxBalance = "simplified-max-balance";
    public const string SimplifiedMaxMonthlyFlow = "simplified-max-monthly-flow";
    public const string SimplifiedFlowWindowDays = "simplified-flow-window-days";
    public const string SimplifiedAlertPct = "simplified-alert-pct";

    // ── Periodic review ─────────────────────────────────────────────────────
    public const string ReviewYearsHigh = "review-years-high";
    public const string ReviewYearsStandard = "review-years-standard";
    public const string ReviewYearsLow = "review-years-low";
    public const string ReviewGraceDays = "review-grace-days";

    // ── Verification ────────────────────────────────────────────────────────
    public const string FaceMatchMaxAttempts = "face-match-max-attempts";
    public const string VerificationRejectionFloor = "verification-rejection-floor";

    public const string TypeString = "string";
    public const string TypeInt = "int";
    public const string TypeBool = "bool";
    public const string TypeDecimal = "decimal";

    /// <summary>Factory defaults, seeded once per tenant. Order is the admin-screen order.</summary>
    public static readonly IReadOnlyList<KycSettingDefault> Defaults =
    [
        new(SimplifiedMaxBalance, "250000", TypeDecimal,
            "Plafond de solde d'un client en KYC simplifié (XOF)."),
        new(SimplifiedMaxMonthlyFlow, "500000", TypeDecimal,
            "Plafond de flux (dépôts + retraits) sur la fenêtre glissante (XOF)."),
        new(SimplifiedFlowWindowDays, "30", TypeInt,
            "Largeur de la fenêtre de calcul du flux, en jours glissants."),
        new(SimplifiedAlertPct, "80", TypeInt,
            "Pourcentage du plafond de flux qui déclenche l'alerte à l'agent."),

        new(ReviewYearsHigh, "1", TypeInt,
            "Périodicité de revue pour un risque élevé, en années."),
        new(ReviewYearsStandard, "3", TypeInt,
            "Périodicité de revue pour un risque standard, en années."),
        new(ReviewYearsLow, "5", TypeInt,
            "Périodicité de revue pour un risque faible, en années."),
        new(ReviewGraceDays, "30", TypeInt,
            "Délai de grâce après l'échéance avant expiration du dossier."),

        new(FaceMatchMaxAttempts, "2", TypeInt,
            "Nombre d'essais de comparaison faciale avant validation obligatoire du chef d'agence."),
        new(VerificationRejectionFloor, "40", TypeInt,
            "Score de confiance en dessous duquel le dossier repart en complément au lieu d'entrer "
            + "dans le circuit d'approbation."),
    ];

    private static readonly Dictionary<string, KycSettingDefault> ByKey =
        Defaults.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The declared default, or null when the key is not part of the closed list.</summary>
    public static KycSettingDefault? Find(string key)
        => ByKey.TryGetValue(key, out var found) ? found : null;
}
