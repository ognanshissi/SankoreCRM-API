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

    /// <summary>
    /// Currency the two ceilings above are expressed in, ISO 4217. They are stored as bare
    /// decimals, so without this a reader has to assume one — and "XOF" is an assumption, not a
    /// fact, for a tenant outside the franc zone.
    /// </summary>
    public const string CapsCurrency = "caps-currency";

    // ── Periodic review ─────────────────────────────────────────────────────
    public const string ReviewYearsHigh = "review-years-high";
    public const string ReviewYearsStandard = "review-years-standard";
    public const string ReviewYearsLow = "review-years-low";
    public const string ReviewGraceDays = "review-grace-days";

    // ── Verification ────────────────────────────────────────────────────────
    /// <summary>
    /// How recent a proof of address must be, in days, for a REMOTE sale (CIMA LBC/FT article
    /// 13.6 asks for « moins de trois mois »). A parameter rather than a constant: the text is
    /// being reformed, and a tenant's own supervisor may ask for less.
    /// </summary>
    public const string ProofOfAddressMaxAgeDays = "proof-of-address-max-age-days";

    public const string FaceMatchMaxAttempts = "face-match-max-attempts";
    public const string VerificationRejectionFloor = "verification-rejection-floor";

    // ── Remote collection (KRM-B-03) ────────────────────────────────────────

    /// <summary>
    /// How long after a conversion the collection-link email is sent, in minutes.
    ///
    /// <para>
    /// It exists to ORDER two messages, not to pace anything. The welcome email carries the client
    /// number the customer will be asked for on the public page, and both messages are queued
    /// within the same second by two different modules — so without a delay the link can easily
    /// arrive first, and the customer reaches an identification form asking for a number they have
    /// not received. Five minutes is generous for a mail queue and still short enough that nobody
    /// has closed their inbox.
    /// </para>
    /// </summary>
    public const string RemoteEmailDelayMinutes = "remote-email-delay-minutes";

    /// <summary>
    /// Lifetime of the session a customer gets after identifying, in minutes.
    ///
    /// <para>
    /// It bounds how long a phone left on a café table can keep uploading into somebody's KYC
    /// file. Twenty minutes is enough to photograph a card and take a selfie without hurrying;
    /// longer buys the customer nothing, because an expired session sends them back to the
    /// identification screen and KEEPS the photos they already sent (KRM-F-03).
    /// </para>
    /// </summary>
    public const string RemoteSessionMinutes = "remote-session-minutes";

    /// <summary>
    /// Failed identification attempts before the LINK is locked.
    ///
    /// <para>
    /// On the link and not on the customer: locking a customer out would hand anybody holding a
    /// stale link the power to freeze a real enrolment. It is the per-token rate limit — and a
    /// durable one, unlike a sliding window in memory, which is why the public endpoints rely on
    /// this rather than on the rate limiter for the guessing attack that actually matters.
    /// </para>
    /// </summary>
    public const string RemoteMaxIdentifyAttempts = "remote-max-identify-attempts";

    /// <summary>
    /// How many times the customer may retake each photo before the quality gate stops refusing.
    ///
    /// <para>
    /// A BUDGET, not a wall: past it the upload is accepted anyway and the file carries a flag for
    /// the validator. Refusing outright would strand a customer whose phone camera the service
    /// simply does not like, with no way forward and no human to ask — the one outcome a remote
    /// journey must not produce.
    /// </para>
    /// </summary>
    public const string RemoteMaxQualityRetries = "remote-max-quality-retries";

    /// <summary>
    /// Hours a remotely collected file may wait for its responsible agent's decision before they
    /// are chased, with a copy to the branch manager (KRM-B-09).
    ///
    /// <para>
    /// It measures from the ROUTING, not from the submission: the customer's part was over when
    /// they pressed send, and the clock that matters to them is how long the institution then
    /// took. A file approved automatically never starts it — there is nobody to chase.
    /// </para>
    /// </summary>
    public const string RemoteReviewSlaHours = "remote-review-sla-hours";

    /// <summary>
    /// Whether a customer must also type a code received by SMS before their session opens
    /// (KRM-B-12).
    ///
    /// <para>
    /// <b>It can only be turned on by a tenant that has an SMS channel.</b>
    /// <c>UpdateKycSettingHandler</c> refuses the write while
    /// <c>IKycOtpSender.IsAvailableAsync</c> says no — which means a provider, a sender identity,
    /// a credential in the vault and a calling code, all configured in the SMS settings. Enabling
    /// it without them would put every customer of the tenant in front of a code screen for a
    /// message that is never sent, so the administrator is told at the moment they try instead of
    /// their customers finding out.
    /// </para>
    /// </summary>
    public const string RemoteOtp = "remote-otp";

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
        // Vide : la monnaie de l'institution (profil de l'entreprise), celle de tous ses montants.
        new(CapsCurrency, "", TypeString,
            "Devise des plafonds du KYC simplifié (code ISO 4217). Vide : la monnaie de l'institution."),

        new(ReviewYearsHigh, "1", TypeInt,
            "Périodicité de revue pour un risque élevé, en années."),
        new(ReviewYearsStandard, "3", TypeInt,
            "Périodicité de revue pour un risque standard, en années."),
        new(ReviewYearsLow, "5", TypeInt,
            "Périodicité de revue pour un risque faible, en années."),
        new(ReviewGraceDays, "30", TypeInt,
            "Délai de grâce après l'échéance avant expiration du dossier."),

        new(ProofOfAddressMaxAgeDays, "90", TypeInt,
            "Maximum age, in days, of the proof of address required on a remote sale "
            + "(CIMA LBC/FT article 13.6)."),
        new(FaceMatchMaxAttempts, "2", TypeInt,
            "Nombre d'essais de comparaison faciale avant validation obligatoire du chef d'agence."),
        new(VerificationRejectionFloor, "40", TypeInt,
            "Score de confiance en dessous duquel le dossier repart en complément au lieu d'entrer "
            + "dans le circuit d'approbation."),

        new(RemoteEmailDelayMinutes, "5", TypeInt,
            "Délai, en minutes après la conversion, avant l'envoi du lien de dépôt des pièces à "
            + "distance. Laisse arriver d'abord l'email de bienvenue, qui porte le numéro client "
            + "demandé sur la page publique."),
        new(RemoteSessionMinutes, "20", TypeInt,
            "Durée de la session du client après son identification, en minutes."),
        new(RemoteMaxIdentifyAttempts, "5", TypeInt,
            "Nombre d'échecs d'identification avant verrouillage du lien."),
        new(RemoteMaxQualityRetries, "5", TypeInt,
            "Nombre de reprises de photo par document avant que le contrôle qualité cesse de "
            + "refuser ; au-delà, le dépôt est accepté et signalé au validateur."),
        new(RemoteReviewSlaHours, "24", TypeInt,
            "Délai, en heures, au-delà duquel un dossier à distance en attente de décision "
            + "relance l'agent responsable, avec copie au chef d'agence."),
        new(RemoteOtp, "false", TypeBool,
            "Exiger un code reçu par SMS en plus du numéro client et de la date de naissance. "
            + "Nécessite un canal SMS configuré (fournisseur, expéditeur, clé, indicatif) : "
            + "l'activation est refusée sans lui."),
    ];

    private static readonly Dictionary<string, KycSettingDefault> ByKey =
        Defaults.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keys whose value is baked into the per-customer ceilings cache of <c>KycModuleFacade</c>.
    /// Writing one of them must drop that cache, or the new ceiling only bites when the entries
    /// expire — up to five minutes during which operations are measured against the old policy.
    /// </summary>
    public static bool AffectsLimits(string key)
        => key.Equals(SimplifiedMaxBalance, StringComparison.OrdinalIgnoreCase)
        || key.Equals(SimplifiedMaxMonthlyFlow, StringComparison.OrdinalIgnoreCase)
        || key.Equals(SimplifiedFlowWindowDays, StringComparison.OrdinalIgnoreCase)
        || key.Equals(SimplifiedAlertPct, StringComparison.OrdinalIgnoreCase);

    /// <summary>The declared default, or null when the key is not part of the closed list.</summary>
    public static KycSettingDefault? Find(string key)
        => ByKey.TryGetValue(key, out var found) ? found : null;
}
