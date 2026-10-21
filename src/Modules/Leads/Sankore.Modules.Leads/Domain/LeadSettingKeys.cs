namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// One tenant-level setting of the Leads module, as seeded on tenant creation.
/// <paramref name="ValueType"/> is one of <c>"string"</c>, <c>"int"</c>, <c>"bool"</c>,
/// <c>"decimal"</c> or <c>"json"</c> and drives both parsing and the admin UI widget.
/// </summary>
public sealed record LeadSettingDefault(string Key, string Value, string ValueType, string Description);

/// <summary>
/// Keys of the M13 tenant settings plus their factory defaults.
/// Never hard-code a key string anywhere else — always reference a constant from here,
/// otherwise a typo silently resolves to <c>SETTING_UNKNOWN</c>.
/// </summary>
public static class LeadSettingKeys
{
    /// <summary>
    /// Days a health questionnaire response is retained AFTER it has been transmitted to the
    /// insurance back-office (BNF-B-09). Not a general lead retention: the clock starts at
    /// transmission, because until then the response is the only copy and purging it would lose
    /// the subscription.
    /// </summary>
    public const string HealthResponseRetentionDays = "health-response-retention-days";

    /// <summary>
    /// Whether a lead with no resolved product line is REFUSED conversion (BNF-B-04 §3.3), as
    /// opposed to merely being told about it.
    ///
    /// <para>
    /// <b>Default false, and that is a deliberate departure from the specification's default.</b>
    /// BNF-B-04 asks for the refusal outright. But <c>Lead.ProductLineId</c> is new, resolution
    /// from <c>InterestedProduct</c> is best-effort and exact-match only, and the pull job and the
    /// system capture endpoint write the literal label <c>"Unknown"</c> when a source names no
    /// product — which resolves to nothing by design. Shipping the refusal on by default would
    /// therefore make every lead already in the database, and every lead from an unlabelled
    /// source, unconvertible on the day of the deploy. That is not a compliance improvement, it is
    /// an outage.
    /// </para>
    ///
    /// <para>
    /// The gap is REPORTED either way — it is the first item of the completeness report with or
    /// without this switch — so nobody is surprised by it later. Turning it on is the operational
    /// step that completes BNF-B-04, and it belongs AFTER a tenant has configured its catalogue
    /// and its label mappings.
    /// </para>
    /// </summary>
    public const string RequireProductLineToConvert = "require-product-line-to-convert";

    /// <summary>
    /// Months a CLOSED lead's personal data is kept before it is destroyed (BNF-B-19). The clock
    /// starts when the lead reached a terminal status — converted, lost, disqualified or archived —
    /// and a live lead is never anonymised however old it is.
    ///
    /// <para>
    /// Default 24, the specification's figure. A tenant whose regulator asks for longer raises it;
    /// zero or negative is treated as "not configured" and SKIPS the run rather than destroying a
    /// lead the moment it closes, because that is what a settings typo produces by accident and
    /// the mistake is irreversible.
    /// </para>
    /// </summary>
    public const string LeadRetentionMonths = "lead-retention-months";

    /// <summary>
    /// Part des étapes que l'institution attend dans les délais, en pourcentage. Affichée comme
    /// objectif sur l'onglet Performance de l'agent ; elle ne bloque ni n'escalade rien.
    /// </summary>
    public const string SlaComplianceTargetPercent = "sla-compliance-target-percent";

    /// <summary>
    /// Part du délai de premier contact écoulée, en pourcentage, à partir de laquelle l'agent est
    /// prévenu que son SLA va être dépassé (<c>CheckSlaBreachesJob</c>). Exprimée en part du délai
    /// et non en heures : un même seuil reste juste pour un SLA de 2 h comme pour un de 48 h.
    /// 0 désactive la préalerte, et une valeur hors de 1–99 aussi — prévenir à 100 % serait
    /// prévenir au moment du dépassement, que l'alerte de dépassement couvre déjà.
    /// </summary>
    public const string SlaWarningThresholdPercent = "sla-warning-threshold-percent";

    /// <summary>
    /// Whether a WhatsApp message from a number that matches no open lead and no client creates a
    /// lead (WA-BE-09) — source WhatsApp, named from the sender's profile, filed in the receiving
    /// number's agency. Off, the conversation is kept unattached for an agent to file.
    ///
    /// <para>
    /// <b>Default true</b>, and that answers one of the specification's open questions in the
    /// direction that loses nobody: a prospect who writes first is the warmest lead an institution
    /// gets, and an unattached conversation is one nobody sees until the inbox of lot 2 exists.
    /// </para>
    /// </summary>
    public const string WhatsAppCreateLeadForUnknownNumber = "whatsapp-create-lead-for-unknown-number";

    /// <summary>
    /// Minutes an agent has to take over a WhatsApp conversation once the customer is waiting for
    /// a person (WA-BE-14). Past it, the supervisor is told and the lead is redispatched to another
    /// agent (WA-BE-15). 0 disables the SLA.
    /// </summary>
    public const string WhatsAppTakeoverSlaMinutes = "whatsapp-takeover-sla-minutes";

    /// <summary>
    /// What the customer is told, automatically, when their message goes to a person and nobody
    /// holds the conversation yet (WA-BE-14, « message d'attente au lead, paramétrable »). Empty
    /// sends nothing.
    /// </summary>
    public const string WhatsAppWaitingMessage = "whatsapp-waiting-message";

    /// <summary>
    /// What a customer who wrote STOP is told (WA-BE-18). Sent even though their consent is now
    /// withdrawn: it is the acknowledgement of the withdrawal itself.
    /// </summary>
    public const string WhatsAppOptOutConfirmation = "whatsapp-optout-confirmation";

    /// <summary>
    /// Whether the WhatsApp bot answers for this institution at all (WA-BE-12, « activable par
    /// tenant »). Default FALSE: a machine speaking in the institution's name is a decision an
    /// administrator takes, not one a deployment takes for them. Off, every customer message goes
    /// straight to a person, whatever the product profiles say.
    /// </summary>
    public const string WhatsAppBotEnabled = "whatsapp-bot-enabled";

    /// <summary>
    /// Months of silence after which a WhatsApp conversation filed on NO lead — a client's, an
    /// unattached number's — is anonymised (WA-BE-21). A lead's conversations follow the lead's
    /// own retention instead. Zero or negative skips the run.
    /// </summary>
    public const string WhatsAppRetentionMonths = "whatsapp-retention-months";

    /// <summary>
    /// Days a WhatsApp media (photo, document, voice note) is kept, in our store and at the
    /// provider (WA-BE-21). Shorter than the conversation's by design: it is the most sensitive
    /// part of a thread. Zero or negative skips the purge.
    /// </summary>
    public const string WhatsAppMediaRetentionDays = "whatsapp-media-retention-days";

    /// <summary>
    /// What one template costs, per Meta category, for the cost estimate of the metrics (WA-BE-22):
    /// a JSON object such as <c>{"MARKETING": 45, "UTILITY": 10}</c>. Empty by default, and the
    /// metrics then show counts without inventing a price — Meta's rates depend on the country and
    /// change, and a wrong figure on a dashboard is worse than none.
    /// </summary>
    public const string WhatsAppTemplateUnitPrices = "whatsapp-template-unit-prices";

    /// <summary>The currency of <see cref="WhatsAppTemplateUnitPrices"/>.</summary>
    public const string WhatsAppPriceCurrency = "whatsapp-price-currency";

    // ── Value types ─────────────────────────────────────────────────────────
    public const string TypeString  = "string";
    public const string TypeInt     = "int";
    public const string TypeBool    = "bool";
    public const string TypeDecimal = "decimal";
    public const string TypeJson    = "json";

    /// <summary>Factory defaults, seeded once per tenant. Order is the admin-screen order.</summary>
    public static readonly IReadOnlyList<LeadSettingDefault> Defaults =
    [
        new(HealthResponseRetentionDays, "90", TypeInt,
            "Days a health questionnaire response is kept after transmission to the insurance back-office."),
        new(RequireProductLineToConvert, "false", TypeBool,
            "Refuse to convert a lead whose product line has not been resolved. Enable once the "
            + "product catalogue and the label mappings are configured."),
        new(LeadRetentionMonths, "24", TypeInt,
            "Months a closed lead's personal data is kept before it is anonymised. The clock "
            + "starts at closure; a live lead is never anonymised."),
        new(SlaComplianceTargetPercent, "90", TypeInt,
            "Share of steps the institution expects within their deadline, in percent. Shown as "
            + "the target on each agent's performance tab."),
        new(SlaWarningThresholdPercent, "75", TypeInt,
            "Share of the first-contact deadline elapsed, in percent, at which the agent is warned "
            + "that the SLA is about to be breached. 0 disables the warning."),
        new(WhatsAppCreateLeadForUnknownNumber, "true", TypeBool,
            "Create a lead when an unknown number writes to the institution's WhatsApp number. "
            + "Off, the conversation stays unattached until an agent files it."),
        new(WhatsAppTakeoverSlaMinutes, "30", TypeInt,
            "Minutes an agent has to take over a WhatsApp conversation once the customer waits for a "
            + "person. Past it, the supervisor is told and the lead is redispatched. 0 disables it."),
        new(WhatsAppWaitingMessage,
            "Merci pour votre message. Un conseiller vous répond dans les plus brefs délais.", TypeString,
            "Sent automatically when a customer's WhatsApp message waits for an agent. Empty sends nothing."),
        new(WhatsAppOptOutConfirmation,
            "C’est noté : vous ne recevrez plus de messages de notre part sur WhatsApp.", TypeString,
            "Sent to a customer who writes STOP, acknowledging the withdrawal of their consent."),
        new(WhatsAppBotEnabled, "false", TypeBool,
            "Let the WhatsApp bot answer customers, for the products whose bot profile is enabled. "
            + "Off, every message goes straight to an agent."),
        new(WhatsAppRetentionMonths, "24", TypeInt,
            "Months of silence after which a WhatsApp conversation filed on no lead is anonymised. "
            + "A lead's conversations follow the lead's retention. 0 disables it."),
        new(WhatsAppMediaRetentionDays, "180", TypeInt,
            "Days a WhatsApp photo, document or voice note is kept, here and at the provider. "
            + "0 disables the purge."),
        new(WhatsAppTemplateUnitPrices, "{}", TypeJson,
            "Price of one WhatsApp template per Meta category, for the cost estimate, e.g. "
            + "{\"MARKETING\": 45, \"UTILITY\": 10}. Empty: counts only."),
        new(WhatsAppPriceCurrency, "XOF", TypeString,
            "Currency of the WhatsApp template prices."),
    ];

    /// <summary>
    /// The same defaults keyed by setting key. <see cref="Defaults"/> stays the ordered list
    /// because the admin screen renders it in that order; this is the lookup the settings service
    /// and the seeder need. Both views share one declaration, so a new setting cannot appear in
    /// one and be missing from the other.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, LeadSettingDefault> DefaultsByKey =
        Defaults.ToDictionary(d => d.Key, StringComparer.Ordinal);

    private static readonly Dictionary<string, LeadSettingDefault> ByKeyIgnoringCase =
        Defaults.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The declared setting for <paramref name="key"/>, or <c>null</c> when the key is not part of
    /// the closed list.
    ///
    /// <para>
    /// Case-INsensitive, unlike <see cref="DefaultsByKey"/>, because this is the lookup a route
    /// segment goes through and a caller typing <c>Lead-Retention-Months</c> means the same
    /// setting. The canonical <c>Key</c> of the returned record is then what the column is
    /// compared against — string equality in PostgreSQL is case-sensitive, so looking the
    /// catalogue up loosely and the row up strictly is the only combination that behaves.
    /// </para>
    /// </summary>
    public static LeadSettingDefault? Find(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : ByKeyIgnoringCase.GetValueOrDefault(key.Trim());
}
