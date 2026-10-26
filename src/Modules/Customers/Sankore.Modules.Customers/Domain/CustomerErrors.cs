namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// The single source of truth for the error codes returned by the Customers module.
/// <see cref="Sankore.Shared.Kernel.Result"/> only carries a <c>string? Error</c>, so every
/// failure is one of these UPPER_SNAKE codes — the API contract the front-end localizes.
/// </summary>
public static class CustomerErrors
{
    // ── Client lookup & state ───────────────────────────────────────────────
    public const string ClientNotFound = "CLIENT_NOT_FOUND";
    public const string ClientReadOnly = "CLIENT_READ_ONLY";
    public const string ClientUnderMinimumAge = "CLIENT_UNDER_MINIMUM_AGE";
    public const string ClientAlreadyMerged = "CLIENT_ALREADY_MERGED";
    public const string InvalidStatusTransition = "INVALID_STATUS_TRANSITION";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string ReasonRequired = "REASON_REQUIRED";

    // ── Duplicates on capture ───────────────────────────────────────────────
    public const string DuplicateIdentityDocument = "DUPLICATE_IDENTITY_DOCUMENT";
    public const string PossibleDuplicatePhone = "POSSIBLE_DUPLICATE_PHONE";
    public const string DuplicateRegistrationNumber = "DUPLICATE_REGISTRATION_NUMBER";

    // ── Contact points ──────────────────────────────────────────────────────
    public const string LastPhoneRequired = "LAST_PHONE_REQUIRED";
    public const string ContactPointNotFound = "CONTACT_POINT_NOT_FOUND";

    // ── Scope & ownership ───────────────────────────────────────────────────
    public const string AgencyOutOfScope = "AGENCY_OUT_OF_SCOPE";
    public const string AdvisorNotEligible = "ADVISOR_NOT_ELIGIBLE";

    // ── Legal entities ──────────────────────────────────────────────────────
    public const string OwnershipExceeds100 = "OWNERSHIP_EXCEEDS_100";
    public const string ManagerBeneficialOwnerRequired = "MANAGER_BENEFICIAL_OWNER_REQUIRED";
    public const string LegalFormUnknown = "LEGAL_FORM_UNKNOWN";

    // ── Groups ──────────────────────────────────────────────────────────────
    public const string AlreadyInSolidarityGroup = "ALREADY_IN_SOLIDARITY_GROUP";
    public const string GroupSizeLimitReached = "GROUP_SIZE_LIMIT_REACHED";
    public const string GroupNameAlreadyUsed = "GROUP_NAME_ALREADY_USED";
    public const string GroupMemberNotEligible = "GROUP_MEMBER_NOT_ELIGIBLE";
    public const string GroupNotFound = "GROUP_NOT_FOUND";
    public const string MembershipNotFound = "MEMBERSHIP_NOT_FOUND";

    // ── Relationships ───────────────────────────────────────────────────────
    public const string SelfRelationshipForbidden = "SELF_RELATIONSHIP_FORBIDDEN";
    public const string RelationshipNotFound = "RELATIONSHIP_NOT_FOUND";
    public const string RelationshipClosed = "CLIENT_RELATIONSHIP_CLOSED";

    // ── Household (BNF-B-13) ────────────────────────────────────────────────
    public const string NotAHouseholdMember = "NOT_A_HOUSEHOLD_MEMBER";
    public const string HouseholdMemberIsAClient = "HOUSEHOLD_MEMBER_IS_A_CLIENT";
    public const string HouseholdAlreadyHasSpouse = "HOUSEHOLD_ALREADY_HAS_SPOUSE";
    public const string HouseholdMaxChildrenReached = "HOUSEHOLD_MAX_CHILDREN_REACHED";
    public const string HouseholdChildTooOld = "HOUSEHOLD_CHILD_TOO_OLD";
    public const string UnionTypeRequired = "SPOUSE_UNION_TYPE_REQUIRED";
    public const string UnionTypeForbidden = "UNION_TYPE_FORBIDDEN_FOR_CHILD";
    public const string MemberNeedsOwnAddress = "HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS";
    public const string ComplianceSettingsUnavailable = "COMPLIANCE_SETTINGS_UNAVAILABLE";

    // ── Product holdings (BNF-B-14) ─────────────────────────────────────────
    public const string HoldingNotFound = "HOLDING_NOT_FOUND";
    public const string HoldingReadOnly = "HOLDING_READ_ONLY";
    public const string HoldingNotEditable = "HOLDING_NOT_EDITABLE";
    public const string HoldingInvalidTransition = "HOLDING_INVALID_TRANSITION";
    public const string HoldingSyncInProgress = "HOLDING_SYNC_IN_PROGRESS";
    public const string HoldingAlreadyHeld = "HOLDING_ALREADY_HELD";

    public const string SignatureRequired = "HOLDING_SIGNATURE_REQUIRED";
    public const string SignatureReferenceRequired = "SIGNATURE_REFERENCE_REQUIRED";

    /// <summary>
    /// An insurance contract signed before the client received the product's information sheet
    /// (insurance lot 2): the sheet comes first, then the signature.
    /// </summary>
    public const string InformationSheetNotDelivered = "HOLDING_INFORMATION_SHEET_NOT_DELIVERED";
    public const string ExternalRefRequired = "EXTERNAL_REF_REQUIRED";
    public const string ProductLineNotFound = "PRODUCT_LINE_NOT_FOUND";
    public const string ProductLineUnavailable = "PRODUCT_LINE_UNAVAILABLE";

    /// <summary>
    /// The far end answered with a reference that is not the one already recorded (BNF-B-16). Two
    /// external references for one contract means one of the two systems is wrong, and picking a
    /// side silently is the one thing a reconciliation cannot undo.
    /// </summary>
    public const string HoldingExternalRefConflict = "HOLDING_EXTERNAL_REF_CONFLICT";

    // ── Beneficiary designations (BNF-B-15) ─────────────────────────────────
    public const string DesignationRoleNotAllowed = "DESIGNATION_ROLE_NOT_ALLOWED";
    public const string DesignationRequiredRoleMissing = "DESIGNATION_REQUIRED_ROLE_MISSING";
    public const string DesignationTooMany = "DESIGNATION_TOO_MANY";
    public const string DesignationSharesMustTotal100 = "DESIGNATION_SHARES_MUST_TOTAL_100";
    public const string DesignationInvalidShare = "DESIGNATION_INVALID_SHARE";
    public const string DesignationInvalidRank = "DESIGNATION_INVALID_RANK";
    public const string DesignationSubjectInvalid = "DESIGNATION_SUBJECT_INVALID";
    public const string DesignationDuplicateSubject = "DESIGNATION_DUPLICATE_SUBJECT";

    /// <summary>
    /// A <c>HouseholdMember</c> subject that is not an active member of this holding's client's
    /// household (BNF-B-15). Refused rather than stored: a dangling subject reaches an insurer as
    /// a beneficiary with no name, and an id belonging to another client would carry that person's
    /// identity into the payload.
    /// </summary>
    public const string DesignationSubjectNotInHousehold = "DESIGNATION_SUBJECT_NOT_IN_HOUSEHOLD";

    /// <summary>A <c>Client</c> subject this tenant does not have, or one archived or merged.</summary>
    public const string DesignationSubjectNotFound = "DESIGNATION_SUBJECT_NOT_FOUND";

    // ── Sensitive data reveal ───────────────────────────────────────────────
    public const string RevealRateLimitExceeded = "REVEAL_RATE_LIMIT_EXCEEDED";
    public const string UnknownSensitiveField = "UNKNOWN_SENSITIVE_FIELD";

    // ── Merge workflow ──────────────────────────────────────────────────────
    public const string MergeRequestNotFound = "MERGE_REQUEST_NOT_FOUND";
    public const string SelfApprovalForbidden = "SELF_APPROVAL_FORBIDDEN";
    public const string MergeAlreadyDecided = "MERGE_ALREADY_DECIDED";
    public const string SameClientMergeForbidden = "SAME_CLIENT_MERGE_FORBIDDEN";

    // ── Exports & retention ─────────────────────────────────────────────────
    public const string ExportNotFound = "EXPORT_NOT_FOUND";
    public const string ExportLinkExpired = "EXPORT_LINK_EXPIRED";
    public const string RetentionNotReached = "RETENTION_NOT_REACHED";
    public const string KycRetentionNotCleared = "KYC_RETENTION_NOT_CLEARED";

    // ── Erasure on request (data-purge lot 2) ────────────────────────────────
    public const string ErasureNotFound = "ERASURE_REQUEST_NOT_FOUND";
    public const string ErasureSubjectNotFound = "ERASURE_SUBJECT_NOT_FOUND";
    public const string ErasureAlreadyRequested = "ERASURE_ALREADY_REQUESTED";
    public const string ErasureAlreadyDone = "ERASURE_ALREADY_DONE";
    public const string ErasureNotPending = "ERASURE_NOT_PENDING";
    /// <summary>The person who recorded the request cannot be the one who approves or rejects it.</summary>
    public const string ErasureFourEyes = "ERASURE_FOUR_EYES";
    /// <summary>The client's relationship is still active: archive it first; the retention runs from there.</summary>
    public const string ClientRelationshipActive = "CLIENT_RELATIONSHIP_ACTIVE";
    /// <summary>
    /// The person asked to be forgotten and is waiting for the end of their legal retention: no
    /// prospection, no contact, no reading in clear (data-purge lot 3).
    /// </summary>
    public const string ProcessingRestricted = "CLIENT_PROCESSING_RESTRICTED";

    // ── Tenant settings ─────────────────────────────────────────────────────
    public const string SettingUnknown = "SETTING_UNKNOWN";

    /// <summary>
    /// Archiving is blocked because another module reports live commitments on the
    /// client. Raised through <c>IOutstandingBalanceProbe</c>, whose default
    /// implementation answers "no commitments" until M03/M04 expose their contract.
    /// </summary>
    public const string ClientHasActiveCommitments = "CLIENT_HAS_ACTIVE_COMMITMENTS";

    /// <summary>The operation only applies to a <c>ClientType.Legal</c> client.</summary>
    public const string ClientNotLegalEntity = "CLIENT_NOT_LEGAL_ENTITY";

    // ── Profile-change notices (CIMA LBC/FT, 15-day integration deadline) ───
    public const string ProfileNoticeNotFound = "PROFILE_NOTICE_NOT_FOUND";
    public const string ProfileNoticeAlreadyClosed = "PROFILE_NOTICE_ALREADY_CLOSED";
    public const string ProfileNoticeDismissReasonRequired = "PROFILE_NOTICE_DISMISS_REASON_REQUIRED";
    public const string ProfileNoticeIntegratedBeforeNoticed = "PROFILE_NOTICE_INTEGRATED_BEFORE_NOTICED";
}
