namespace Sankore.Modules.Integration.Domain;

/// <summary>
/// The refusals the telephony aggregates raise. Here rather than in
/// <c>IntegrationErrors</c> (PublicApi) because none of them crosses a module boundary: they are
/// answers to a caller inside this module, and the HTTP layer maps them.
/// </summary>
public static class TelephonyErrors
{
    /// <summary>An entity type with no id, or the reverse. The pair is all-or-nothing.</summary>
    public const string EntityReferenceIncomplete = "TELEPHONY_ENTITY_REFERENCE_INCOMPLETE";

    public const string EntityTypeUnknown = "TELEPHONY_ENTITY_TYPE_UNKNOWN";

    /// <summary>
    /// An automatic attachment tried to overwrite one a human made. Refused rather than applied:
    /// a sweep that re-reads a call must not undo an agent's correction.
    /// </summary>
    public const string ManualAttachmentWins = "TELEPHONY_MANUAL_ATTACHMENT_WINS";

    public const string OutcomeRequired = "TELEPHONY_OUTCOME_REQUIRED";

    /// <summary>The wrap-up named a code this tenant's referential does not hold, or holds inactive.</summary>
    public const string OutcomeUnknown = "TELEPHONY_OUTCOME_UNKNOWN";

    /// <summary>No such outcome in this tenant's referential (the settings screen).</summary>
    public const string OutcomeNotFound = "TELEPHONY_OUTCOME_NOT_FOUND";

    /// <summary>
    /// The code is already in the referential — including a DEACTIVATED one, which is the common
    /// case: re-adding a code somebody switched off is a reactivation, not a creation, and
    /// answering "created" would silently keep the old row's behaviour flags.
    /// </summary>
    public const string OutcomeCodeAlreadyUsed = "TELEPHONY_OUTCOME_CODE_ALREADY_USED";

    /// <summary>
    /// The outcome demands a callback date and none was given (TEL-B-11). The flag is the
    /// referential's, so an institution decides which of its outcomes carry the obligation.
    /// </summary>
    public const string CallbackDateRequired = "TELEPHONY_CALLBACK_DATE_REQUIRED";

    /// <summary>Wrapping up a call that has not ended. The agent is still speaking.</summary>
    public const string CallNotEnded = "TELEPHONY_CALL_NOT_ENDED";

    /// <summary>
    /// No such call for this tenant. Answered for another tenant's call too, and never a 403: the
    /// existence of a call is itself information about who the institution spoke to.
    ///
    /// <para>
    /// Also answered for a call of this tenant that the caller may not act on, for the same
    /// reason — a 403 there would confirm that a colleague spoke to somebody.
    /// </para>
    /// </summary>
    public const string CallNotFound = "TELEPHONY_CALL_NOT_FOUND";

    /// <summary>
    /// The caller may not act on the RECORD they asked a call to be attached to (TEL-B-09).
    ///
    /// <para>
    /// A 403 and not a "not found", which is the opposite of this module's usual posture — and
    /// the reason is the one M01 gives on its auto-approval control: the caller can already see
    /// the call, so there is nothing left to hide, and "not found" would send an agent looking
    /// for a record that is in front of them. What is withheld is the record, and that is
    /// withheld by the owning module's own screens.
    /// </para>
    /// </summary>
    public const string EntityNotYours = "TELEPHONY_ENTITY_NOT_YOURS";

    /// <summary>
    /// The whole family is switched off for this tenant (TEL-B-01). Answered by every endpoint
    /// rather than a 404, so an administrator reads a setting to change and not a bug to report.
    /// </summary>
    public const string Disabled = "TELEPHONY_DISABLED";

    /// <summary>A provider account already belongs to another CRM user, or the reverse.</summary>
    public const string UserLinkAlreadyTaken = "TELEPHONY_USER_LINK_ALREADY_TAKEN";

    public const string UserLinkNotFound = "TELEPHONY_USER_LINK_NOT_FOUND";

    /// <summary>
    /// An unlink with no reason. Its own code rather than the "not found" the aggregate answered
    /// when it was first written: an administrator told a link does not exist goes looking for a
    /// row that is in front of them, while the actual fault is an empty field.
    /// </summary>
    public const string UnlinkReasonRequired = "TELEPHONY_UNLINK_REASON_REQUIRED";

    /// <summary>A click-to-call was asked for by a user with no link to the provider.</summary>
    public const string UserNotLinked = "TELEPHONY_USER_NOT_LINKED";

    /// <summary>
    /// The owning module could not be asked for the number — it is down, or the read threw
    /// (TEL-B-06). Distinct from a slot that holds nothing and from a perimeter refusal: the agent
    /// is told to try again rather than told the record has no number, which would send them
    /// editing a contact point that is there.
    /// </summary>
    public const string NumberUnavailable = "TELEPHONY_NUMBER_UNAVAILABLE";

    /// <summary>
    /// The provider refused the call. The agent's screen says so and the request is NOT retried —
    /// a retried call is a telephone ringing twice in somebody's hand.
    /// </summary>
    public const string ProviderRefused = "TELEPHONY_PROVIDER_REFUSED";

    /// <summary>
    /// The journal already resolved this caller, so the call belongs to a lead or a client
    /// (TEL-B-13). Creating a second record for the same person is the one thing the
    /// create-a-lead button must not do.
    /// </summary>
    public const string CallAlreadyAttached = "TELEPHONY_CALL_ALREADY_ATTACHED";

    /// <summary>
    /// The number turned out to belong to a CLIENT. The call is attached to them instead and no
    /// lead is created — a client asking for something is not a prospect.
    /// </summary>
    public const string CallerIsAlreadyAClient = "TELEPHONY_CALLER_IS_ALREADY_A_CLIENT";

    /// <summary>
    /// A lead was asked for from an OUTBOUND call. There is nobody to create: the agent dialled,
    /// so the record is whatever they dialled from.
    /// </summary>
    public const string NotAnInboundCall = "TELEPHONY_NOT_AN_INBOUND_CALL";

    /// <summary>M13 could not be asked, or refused. The call stays as it was.</summary>
    public const string LeadCreationFailed = "TELEPHONY_LEAD_CREATION_FAILED";

    // ── Recordings (TEL-B-15 → TEL-B-17) ─────────────────────────────────

    /// <summary>
    /// A recording mode other than <c>Disabled</c> with no lawful basis stated. The one rule the
    /// policy aggregate exists to hold: a recording kept without one is a file nobody can justify.
    /// </summary>
    public const string RecordingLegalBasisRequired = "TELEPHONY_RECORDING_LEGAL_BASIS_REQUIRED";

    /// <summary>
    /// A basis with no purpose. Different questions — "we may" and "we need to" — and a purpose
    /// limitation nobody wrote down is one nobody can be held to.
    /// </summary>
    public const string RecordingPurposeRequired = "TELEPHONY_RECORDING_PURPOSE_REQUIRED";

    public const string RecordingRetentionInvalid = "TELEPHONY_RECORDING_RETENTION_INVALID";

    public const string RecordingDurationCeilingInvalid =
        "TELEPHONY_RECORDING_DURATION_CEILING_INVALID";

    /// <summary>No recording held for this call. Ordinary: most calls have none.</summary>
    public const string RecordingNotFound = "TELEPHONY_RECORDING_NOT_FOUND";

    /// <summary>
    /// The row exists but the bytes do not — pending retrieval, failed, or purged. Distinct from
    /// "not found" so a screen can say which: "being fetched" and "gone for ever" are different
    /// answers to an agent who is looking for it.
    /// </summary>
    public const string RecordingNotAvailable = "TELEPHONY_RECORDING_NOT_AVAILABLE";

    /// <summary>
    /// An export with no stated reason. Exporting takes a voice recording OUT of the platform,
    /// past every control the audited player applies, so the reason is the only thing that makes
    /// it answerable afterwards.
    /// </summary>
    public const string RecordingExportReasonRequired =
        "TELEPHONY_RECORDING_EXPORT_REASON_REQUIRED";

    /// <summary>Recording is switched off for this tenant, so there is nothing to configure around.</summary>
    public const string RecordingDisabled = "TELEPHONY_RECORDING_DISABLED";

    /// <summary>
    /// A legal hold with no stated reason. The hold is an exception to a retention rule, and an
    /// exception nobody wrote down is one nobody can lift with confidence years later.
    /// </summary>
    public const string RecordingHoldReasonRequired = "TELEPHONY_RECORDING_HOLD_REASON_REQUIRED";
}
