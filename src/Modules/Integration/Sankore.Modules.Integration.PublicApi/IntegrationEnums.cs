namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Which family of back-office a connection talks to. The socle (outbox, dispatcher, batch,
/// relay agent, reconciliation) is shared by both; only the ports differ.
///
/// <para>
/// A tenant has at most ONE active <see cref="CoreBanking"/> connection and at most one active
/// <see cref="Telephony"/> one, and may have several active <see cref="Insurance"/> ones — the
/// partial unique indexes on <c>integration_connection</c> enforce exactly that asymmetry
/// (ASS-01, TEL-B-01). The asymmetry is per family and spelled out per family, because the
/// reasons differ: an IMF has one core banking system and one telephone system, and distributes
/// for several insurers.
/// </para>
/// </summary>
public enum IntegrationFamily
{
    CoreBanking,
    Insurance,

    /// <summary>
    /// The tenant's telephone system (TEL-B-01). SANKORE does not replace it — routing, IVR,
    /// groups and opening hours stay configured at the provider; what this family adds is
    /// click-to-call, a call journal attached to leads and clients, and the screen pop.
    /// </summary>
    Telephony,

    /// <summary>
    /// A conversational messaging channel — the institution's WhatsApp Business number (WA lot 1).
    ///
    /// <para>
    /// <b>No exclusivity index, unlike the two families above.</b> Whether an institution runs one
    /// number or one per branch is still an open question of the specification, and the
    /// connection already carries an optional agency: two active numbers is a configuration this
    /// family must be able to hold. What IS unique is the provider's phone-number id, globally
    /// and not per tenant — it is how an inbound webhook finds its tenant.
    /// </para>
    /// </summary>
    Messaging
}

/// <summary>
/// The product on the other end. One adapter per value, resolved through keyed DI.
///
/// <para>
/// <see cref="Fake"/> is selectable in Development only: it answers plausible successes with no
/// back-office at all, and a deployment that reached it by accident would report writes that
/// never happened.
/// </para>
/// </summary>
public enum IntegrationKind
{
    Temenos,
    Amplitude,
    Sab,
    PerfectVision,
    Orass,

    /// <summary>The first telephony provider (TEL-B-04), on its public API v2.</summary>
    Ringover,
    Fake,

    /// <summary>
    /// 360dialog, the WhatsApp Business Solution Provider of the Partner programme (WA-BE-01). It
    /// fronts Meta's Cloud API: the messages, the statuses and the webhook bodies are Meta's own
    /// format, behind a per-number API key.
    /// </summary>
    Dialog360
}

/// <summary>
/// How SANKORE reaches the system.
///
/// <list type="bullet">
/// <item><see cref="Api"/> — direct HTTP from this process.</item>
/// <item><see cref="Batch"/> — files exchanged over SFTP; a command is <c>Batched</c> rather than
///   sent, and closed later by an acknowledgement (INT-24/INT-25).</item>
/// <item><see cref="Relay"/> — through the on-premise agent, which holds the only outbound
///   connection; SANKORE opens no port into the IMF's network (INT-26).</item>
/// </list>
/// </summary>
public enum IntegrationMode
{
    Api,
    Batch,
    Relay
}

/// <summary>
/// Why a call failed — and therefore what the platform does next. The three families are the
/// whole point of <see cref="IntegrationResult"/>: a retry decided from an HTTP status code
/// scattered across adapters is a retry that eventually hammers a system that refused on the
/// merits.
/// </summary>
public enum ErrorFamily
{
    /// <summary>Timeout, 503, maintenance window, relay unreachable — retried with backoff.</summary>
    Transient,

    /// <summary>
    /// Duplicate, document refused, unknown product — the external system answered, and said no.
    /// Never retried: it goes to the rejection queue for a human.
    /// </summary>
    Functional,

    /// <summary>
    /// Missing mapping, invalid payload, authentication refused — OUR configuration is wrong.
    /// Never retried, and the administrator is alerted: retrying cannot fix it.
    /// </summary>
    Technical
}

/// <summary>Whether a capability is served live or only through the batch cycle.</summary>
public enum CapabilityMode
{
    RealTime,
    Batch
}

/// <summary>
/// What an adapter can actually do. Declared per capability rather than inferred from
/// <see cref="IntegrationKind"/>: the same product answers differently depending on the version
/// installed at the IMF (INT-31 switches on <c>AmplitudeSettings.AmplitudeVersion</c>).
/// </summary>
public enum IntegrationCapability
{
    CreateCustomer,
    UpdateCustomer,
    SetKycLevel,

    /// <summary>
    /// Reading the tier back from the external system. Separate from <see cref="SetKycLevel"/>
    /// because the two are genuinely independent: Temenos Transact lets a party's KYC status be
    /// written and read, while a batch-file CBS accepts the write and exposes no query at all.
    /// Without this capability INT-21 can only infer the tier from our own acknowledged writes,
    /// which cannot see a tier an officer changed inside the CBS.
    /// </summary>
    ReadKycLevel,
    OpenAccount,
    ReadAccounts,
    ReadBalance,
    ReadTransactions,
    ReadMonthlyFlow,
    SubmitLoanApplication,
    ReadLoans,
    DebitAccount,
    ReverseDebit,
    SubscribePolicy,
    ReadPolicies,
    IssueCertificate,
    CancelPolicy,
    DeclareClaim,
    ReadClaims,
    PriceProduct,
    CheckEligibility,

    // ── Telephony (TEL-B-02) ─────────────────────────────────────────────
    //
    // Declared per capability like every other family, and for the same reason: two providers
    // of the same kind of system answer differently. A provider that cannot delete a recording
    // at its end is an ordinary provider, not a broken one — it simply does not claim
    // RecordingDeletion, and the retention stays ours alone.

    /// <summary>Placing an outbound call by making the agent's own handset ring first.</summary>
    ClickToCall,

    /// <summary>Pushing call events to a webhook. Without it the journal is sync-only.</summary>
    CallEvents,

    /// <summary>Reading calls back — what verifies a webhook and what the catch-up sync uses.</summary>
    CallHistory,

    /// <summary>Making a call's recording available for download.</summary>
    Recordings,

    /// <summary>Deleting a recording at the provider once SANKORE holds it.</summary>
    RecordingDeletion,

    /// <summary>Listing the provider's own users, so CRM accounts can be linked to them.</summary>
    UserDirectory,

    // ── Messaging (WA-BE-01) ─────────────────────────────────────────────

    /// <summary>Sending a pre-approved template — the only message allowed outside the 24 h window.</summary>
    TemplateMessages,

    /// <summary>Sending free text inside the 24 h customer-service window.</summary>
    FreeformMessages,

    /// <summary>Sending and receiving images and documents.</summary>
    MediaMessages,

    /// <summary>Pushing inbound messages and delivery statuses to a webhook.</summary>
    MessageEvents,

    /// <summary>Listing the account's templates with their approval status.</summary>
    TemplateCatalog
}

/// <summary>KYC tier as the external system understands it. Mapped from M02's own tiers.</summary>
public enum KycLevel
{
    None,
    Simplified,
    Full
}

/// <summary>
/// Which kind of record a call is attached to (TEL-B-09), as the CONTRACT spells it.
///
/// <para>
/// In the PublicApi and not only in the module's domain because a consumer has to compare against
/// it: <c>TelephonyCallWrappedUpEvent.EntityType</c> is a string, and M13's consumer branching on
/// a literal <c>"Lead"</c> would be a second spelling of this list in another assembly — the kind
/// of drift that makes a consumer silently stop recognising its own events. The module's
/// <c>TelEntityTypes</c> derives its values from here, so there is one definition.
/// </para>
/// </summary>
public static class TelephonyEntityTypes
{
    public const string Lead = "Lead";
    public const string Customer = "Customer";

    public static bool IsKnown(string? value) => value is Lead or Customer;
}
