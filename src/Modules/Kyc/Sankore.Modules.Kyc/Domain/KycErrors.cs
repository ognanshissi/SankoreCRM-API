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
    public const string CorrectionSourceInvalid = "KYC_CORRECTION_SOURCE_INVALID";
    public const string DuplicateClearReasonRequired = "KYC_DUPLICATE_CLEAR_REASON_REQUIRED";
    public const string VerificationImageNotFound = "KYC_VERIFICATION_IMAGE_NOT_FOUND";

    public const string SettingUnknown = "KYC_SETTING_UNKNOWN";
    public const string SettingInvalidValue = "KYC_SETTING_INVALID_VALUE";
}
