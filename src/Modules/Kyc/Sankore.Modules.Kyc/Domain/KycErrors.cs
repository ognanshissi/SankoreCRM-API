namespace Sankore.Modules.Kyc.Domain;

/// <summary>
/// Stable error codes returned by this module's handlers. Codes, not sentences: the front
/// translates them, and a renamed code is a breaking API change.
/// </summary>
public static class KycErrors
{
    public const string FileAlreadyExists = "KYC_FILE_ALREADY_EXISTS";
    public const string FileNotFound = "KYC_FILE_NOT_FOUND";
    public const string InvalidTransition = "KYC_INVALID_TRANSITION";
    public const string ConcurrencyConflict = "KYC_CONCURRENCY_CONFLICT";
    public const string SelfApprovalForbidden = "KYC_SELF_APPROVAL_FORBIDDEN";
    public const string ApprovalStepAlreadyDecided = "KYC_APPROVAL_STEP_ALREADY_DECIDED";
    public const string ApprovalStepNotYours = "KYC_APPROVAL_STEP_NOT_YOURS";
    public const string ApprovalOutOfOrder = "KYC_APPROVAL_OUT_OF_ORDER";
    public const string ReviewNotFound = "KYC_REVIEW_NOT_FOUND";
    public const string ReviewReasonRequired = "KYC_REVIEW_REASON_REQUIRED";
    public const string FileNotOpen = "KYC_FILE_NOT_OPEN";
    public const string IdentityDocumentNotFound = "KYC_IDENTITY_DOCUMENT_NOT_FOUND";
    public const string DocumentNotFound = "KYC_DOCUMENT_NOT_FOUND";

    /// <summary>
    /// A validator tried to decide a document somebody has already decided. Like an approval step,
    /// a document decision is evidence and is not re-taken — the agent uploads a better image,
    /// which is a new row.
    /// </summary>
    public const string DocumentAlreadyReviewed = "KYC_DOCUMENT_ALREADY_REVIEWED";

    /// <summary>Refusing a document without saying why gives the agent nothing to act on.</summary>
    public const string DocumentReasonRequired = "KYC_DOCUMENT_REASON_REQUIRED";

    /// <summary>
    /// The document has already been superseded by a newer upload of the same kind. Refusing it
    /// would send the whole file back over an image nobody is using any more — the same reason a
    /// stale approval screen is told <see cref="ApprovalOutOfOrder"/> instead of signing whatever
    /// rung the file has reached since.
    /// </summary>
    public const string DocumentNotCurrent = "KYC_DOCUMENT_NOT_CURRENT";

    /// <summary>
    /// Validating a file's evidence by hand without a justification. The whole point of the manual
    /// route is that a human overrode the machine, so the record has to say on what grounds.
    /// </summary>
    public const string ManualValidationReasonRequired = "KYC_MANUAL_VALIDATION_REASON_REQUIRED";
    public const string CorrectionSourceInvalid = "KYC_CORRECTION_SOURCE_INVALID";
    public const string DuplicateClearReasonRequired = "KYC_DUPLICATE_CLEAR_REASON_REQUIRED";
    public const string VerificationImageNotFound = "KYC_VERIFICATION_IMAGE_NOT_FOUND";

    // ── Remote collection links (KRM-B-03/04) ──────────────────────────────

    /// <summary>
    /// The file is not in a state that can be collected remotely — already approved, rejected, or
    /// in the middle of a verification. Sending a link would invite a customer to upload evidence
    /// for a decision that has been taken.
    /// </summary>
    public const string RemoteInvitationFileNotCollecting = "KYC_REMOTE_INVITATION_FILE_NOT_COLLECTING";

    /// <summary>
    /// The product this file was converted on does not allow remote collection — or the file names
    /// no product at all. Named rather than folded into a generic refusal because what the agent
    /// needs to be told is that the CATALOGUE forbids it, not that something went wrong.
    /// </summary>
    public const string RemoteInvitationProductNotEligible = "KYC_REMOTE_INVITATION_PRODUCT_NOT_ELIGIBLE";

    /// <summary>
    /// The customer record carries no email address, so there is nowhere to send the link. The
    /// ordinary case for a lead captured in the field with a phone number and nothing else — the
    /// agent adds an address and re-issues.
    /// </summary>
    public const string RemoteInvitationNoEmail = "KYC_REMOTE_INVITATION_NO_EMAIL";

    public const string RemoteInvitationNotFound = "KYC_REMOTE_INVITATION_NOT_FOUND";

    /// <summary>
    /// <see cref="KycRemoteInvitation.MarkSent"/> on a link that is not waiting to be sent. The
    /// delayed send job is retried by Hangfire, and one link must produce one email.
    /// </summary>
    public const string RemoteInvitationNotPending = "KYC_REMOTE_INVITATION_NOT_PENDING";

    /// <summary>
    /// Withdrawing a link without saying why. The customer loses their way in, and a support call
    /// about it has nothing to go on.
    /// </summary>
    public const string RemoteInvitationRevokeReasonRequired = "KYC_REMOTE_INVITATION_REVOKE_REASON_REQUIRED";

    public const string SettingUnknown = "KYC_SETTING_UNKNOWN";
    public const string SettingInvalidValue = "KYC_SETTING_INVALID_VALUE";

    /// <summary>
    /// Well-typed but meaningless — a zero flow window, a 150 % alert threshold. Separate from
    /// <see cref="SettingInvalidValue"/> so a screen can say "hors limites" instead of "mal typé",
    /// which sends an administrator looking for a formatting mistake that is not there.
    /// </summary>
    public const string SettingValueOutOfRange = "KYC_SETTING_VALUE_OUT_OF_RANGE";

    /// <summary>
    /// A remotely-opened file (<c>Web</c> or <c>MobileAgent</c>) reached its final approval with
    /// no accepted, recent proof of address — CIMA LBC/FT article 13.6. Named rather than folded
    /// into a generic refusal: what the validator needs to be told is which document to ask for.
    /// </summary>
    public const string ProofOfAddressRequired = "KYC_PROOF_OF_ADDRESS_REQUIRED";

    /// <summary>
    /// A POLITICALLY EXPOSED file reached its final approval while the client record carried no
    /// recorded origin of patrimony and funds — which the CIMA LBC/FT text requires to be
    /// established BEFORE the relationship is entered into. Also what an outage reading it
    /// answers: the gate fails closed.
    /// </summary>
    public const string FundsOriginRequired = "KYC_FUNDS_ORIGIN_REQUIRED";
}
