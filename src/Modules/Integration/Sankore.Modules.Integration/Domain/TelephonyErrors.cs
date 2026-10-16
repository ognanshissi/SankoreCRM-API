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

    /// <summary>Wrapping up a call that has not ended. The agent is still speaking.</summary>
    public const string CallNotEnded = "TELEPHONY_CALL_NOT_ENDED";

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
}
