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
    /// The link cannot carry what was asked of it — expired, revoked, locked, or already spent.
    /// One code for the four because this one is returned to a SESSION caller, who has already
    /// been told the state of their link by the status probe; the public status endpoint is where
    /// the four are distinguished.
    /// </summary>
    public const string RemoteInvitationNotUsable = "KYC_REMOTE_INVITATION_NOT_USABLE";

    /// <summary>
    /// Recording consent without naming the version of the text that was shown. Consent to a text
    /// nobody can identify afterwards is not evidence of anything.
    /// </summary>
    public const string RemoteConsentVersionRequired = "KYC_REMOTE_CONSENT_VERSION_REQUIRED";

    /// <summary>
    /// A document was uploaded, or the file submitted, before the customer accepted the processing
    /// of their biometric data. The consent is the lawful basis for everything that follows it, so
    /// it gates the first upload rather than the submission.
    /// </summary>
    public const string RemoteConsentRequired = "KYC_REMOTE_CONSENT_REQUIRED";

    /// <summary>
    /// The product does not accept the identity document the customer chose. Not a validation
    /// error on a free-form field: the page offers the list the product declares, so reaching this
    /// means the list was bypassed.
    /// </summary>
    public const string RemoteDocumentTypeNotAllowed = "KYC_REMOTE_DOCUMENT_TYPE_NOT_ALLOWED";

    /// <summary>
    /// The customer tried to send their file while a document the product requires is missing. The
    /// response lists which.
    /// </summary>
    public const string RemoteDocumentsMissing = "KYC_REMOTE_DOCUMENTS_MISSING";

    /// <summary>
    /// A selfie was uploaded before the identity document. The quality check on a selfie is a
    /// face-match call against the document portrait, so there is nothing to check it against —
    /// and the journey presents them in that order anyway.
    /// </summary>
    public const string RemoteIdentityDocumentFirst = "KYC_REMOTE_IDENTITY_DOCUMENT_FIRST";

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

    /// <summary>
    /// <see cref="KycFile.ApproveAutomatically"/> was reached on a file that was NOT collected by
    /// the customer themselves (KRM-B-08).
    ///
    /// <para>
    /// It is a bug guard rather than a business outcome: the router only ever calls it on a
    /// <c>RemoteSelfService</c> file. It exists because that method is the single place in the
    /// module where a tier is granted with no human, so "the caller already checked" is not a good
    /// enough guarantee — on any other channel an employee produced the evidence, and the
    /// four-eyes rule on their submission is exactly what must not be walked around.
    /// </para>
    /// </summary>
    public const string AutoApprovalChannelNotEligible = "KYC_AUTO_APPROVAL_CHANNEL_NOT_ELIGIBLE";

    /// <summary>
    /// A complement was requested naming a document kind the product's remote policy does not
    /// collect (KRM-B-10). The kinds offered to the agent come from the file's own journey, so
    /// this is a stale screen or a hand-made request.
    /// </summary>
    public const string ComplementDocumentNotCollectable = "KYC_COMPLEMENT_DOCUMENT_NOT_COLLECTABLE";

    /// <summary>
    /// A sampled automatic approval whose control has already been recorded (KRM-B-11). A recorded
    /// finding is evidence that a control was performed, and re-recording it would rewrite who
    /// found what — an officer who changes their mind raises a review on the file instead.
    /// </summary>
    public const string AutoApprovalControlAlreadyRecorded = "KYC_AUTO_APPROVAL_CONTROL_ALREADY_RECORDED";

    /// <summary>
    /// A NON-COMPLIANT verdict with no finding. It opens an event-driven review that a colleague
    /// will pick up, and "non conforme" alone tells them nothing to act on.
    /// </summary>
    public const string AutoApprovalControlFindingRequired = "KYC_AUTO_APPROVAL_CONTROL_FINDING_REQUIRED";

    public const string AutoApprovalControlNotFound = "KYC_AUTO_APPROVAL_CONTROL_NOT_FOUND";

    // ── The SMS second factor (KRM-B-12) ───────────────────────────────────

    /// <summary>
    /// <c>remote-otp</c> cannot be turned on: this TENANT has no usable SMS channel, so a second
    /// factor would be a code screen for a message that is never sent. Named rather than folded
    /// into a generic range error so the administrator's screen can point them at the SMS settings
    /// instead of telling them "true" was the wrong value.
    ///
    /// <para>
    /// It is also what the identification endpoint answers (503) when a code could not be
    /// delivered mid-journey: the customer is asked to try again rather than being handed a
    /// session the tenant's own policy says they should not have.
    /// </para>
    /// </summary>
    public const string RemoteOtpUnavailable = "KYC_REMOTE_OTP_UNAVAILABLE";

    /// <summary>
    /// The customer's code was wrong, stale, or they have used up their three tries. ONE code for
    /// all of them on the public surface, for the reason
    /// <see cref="RemoteInvitationNotUsable"/> exists: telling a caller which it was turns the
    /// endpoint into an oracle on whether a code is currently live.
    /// </summary>
    public const string RemoteOtpInvalid = "KYC_REMOTE_OTP_INVALID";

    /// <summary>
    /// A code was asked for on a link that already has a live one and plenty of window left. A
    /// bound on resending, so a script cannot make the platform send a customer a text message
    /// every second — and on a channel that is billed per message, that bound is also the budget.
    /// </summary>
    public const string RemoteOtpTooSoon = "KYC_REMOTE_OTP_TOO_SOON";
}
