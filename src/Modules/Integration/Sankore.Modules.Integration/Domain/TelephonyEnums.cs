namespace Sankore.Modules.Integration.Domain;

/// <summary>
/// Which way a call went. Decided by the provider and recorded as it said, never inferred from
/// which number we recognise — a client calling from their partner's handset is an inbound call
/// whatever the caller id resolves to.
/// </summary>
public enum TelCallDirection
{
    Inbound,
    Outbound,
}

/// <summary>
/// Where a call got to (TEL-B-08, §4.1).
///
/// <para>
/// Six values and not a pair of booleans, because an agent looking at a lead nobody reached needs
/// to know WHERE it stopped: a call that rang and was not picked up is a customer who may call
/// back, one that went to voicemail is a message waiting, and one that failed is an operator
/// refusal or a wrong number — three different next actions.
/// </para>
/// </summary>
public enum TelCallStatus
{
    /// <summary>Ringing. The only status a webhook may write without re-reading the provider.</summary>
    Ringing,

    /// <summary>Picked up and still in progress.</summary>
    InProgress,

    /// <summary>Ended after having been picked up.</summary>
    Completed,

    /// <summary>Inbound and never picked up, or outbound and never answered.</summary>
    Missed,

    /// <summary>Diverted to the mailbox. Treated as missed for the agent's follow-up list.</summary>
    Voicemail,

    /// <summary>Technical failure — invalid number, operator refusal.</summary>
    Failed,
}

/// <summary>
/// Where a click-to-call request got to (TEL-B-06, §4.2). The request is NOT the call: it is the
/// few seconds between an agent clicking and the provider making their handset ring. Once the two
/// are correlated, <see cref="TelCall"/> carries everything that follows.
/// </summary>
public enum TelClickToCallStatus
{
    Requested,
    AgentRinging,

    /// <summary>Correlated with a provider call. Nothing more is written here.</summary>
    Connected,

    /// <summary>The provider refused, or a pre-flight check did.</summary>
    Failed,

    /// <summary>Two minutes with no event. Not a failure of the provider — a silence.</summary>
    Expired,
}

/// <summary>
/// Whether a call was attached to a record, and how sure that is (TEL-B-09, §4.3).
/// </summary>
public enum TelMatchStatus
{
    /// <summary>Nothing matched the remote number.</summary>
    Unknown,

    /// <summary>Exactly one record matched.</summary>
    Matched,

    /// <summary>
    /// Several records share the number. Left for a human rather than resolved by a rule: a
    /// family handset or a shop line is ordinary here, and guessing would file a conversation
    /// against the wrong person.
    /// </summary>
    Ambiguous,

    /// <summary>Both ends are linked users of this tenant. Excluded from the SLA and the stats.</summary>
    Internal,
}

/// <summary>How the attachment was decided. Evidence, not a confidence score.</summary>
public enum TelMatchMethod
{
    /// <summary>The agent said which record they were calling. The only certain one.</summary>
    ClickToCall,

    /// <summary>The remote number resolved through a blind index.</summary>
    Number,

    /// <summary>A human attached or corrected it, with a reason.</summary>
    Manual,
}

/// <summary>
/// What SANKORE holds of a call's recording (TEL-B-16, §4.3). Distinct from whether the PROVIDER
/// recorded: a tenant whose mode is <c>Disabled</c> leaves every call <see cref="NotRecorded"/>
/// however many recordings the provider produced.
/// </summary>
public enum TelRecordingStatus
{
    NotRecorded,

    /// <summary>Known to exist at the provider and queued for retrieval.</summary>
    Pending,

    /// <summary>Stored, encrypted, and readable through the audited stream.</summary>
    Available,

    /// <summary>Retrieval failed for good. The provider may still hold it.</summary>
    Failed,

    /// <summary>Retention reached, or the person was anonymised. File and key are gone.</summary>
    Purged,
}

/// <summary>The link between a CRM user and their account at the provider (TEL-B-05).</summary>
public enum TelUserLinkStatus
{
    Active,

    /// <summary>
    /// The provider account vanished or was disabled. Kept rather than deleted: an agent whose
    /// link broke still has calls in the journal, and the row is what explains why no new one
    /// is being attributed to them.
    /// </summary>
    Broken,

    /// <summary>Switched off here — the CRM user left, or an administrator unlinked them.</summary>
    Disabled,
}

/// <summary>
/// Which kind of record a call is attached to. A STRING pair with the id, never a foreign key:
/// leads and clients live in other schemas, and a dangling reference must degrade rather than
/// block a delete somebody else's module is entitled to make.
/// </summary>
public static class TelEntityTypes
{
    public const string Lead = "Lead";
    public const string Customer = "Customer";

    public static bool IsKnown(string? value)
        => value is Lead or Customer;
}
