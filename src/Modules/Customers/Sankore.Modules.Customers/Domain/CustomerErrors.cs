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
}
