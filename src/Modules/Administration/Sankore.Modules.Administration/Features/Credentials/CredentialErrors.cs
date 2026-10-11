namespace Sankore.Modules.Administration.Features.Credentials;

/// <summary>
/// Stable UPPER_SNAKE codes of this area. They repeat the domain's own constants rather than
/// introducing a second spelling, because a caller cannot tell which layer refused.
/// </summary>
internal static class CredentialErrors
{
    public const string NotFound = "CREDENTIAL_NOT_FOUND";
    public const string UserNotFound = "CREDENTIAL_USER_NOT_FOUND";
    public const string AlreadyClosed = "CREDENTIAL_ALREADY_CLOSED";
    public const string CloseReasonRequired = "CREDENTIAL_CLOSE_REASON_REQUIRED";
    public const string DuplicateCardNumber = "CREDENTIAL_CARD_NUMBER_ALREADY_RECORDED";

    /// <summary>
    /// A renewal was asked for without saying what happened to the card it replaces. Refused
    /// rather than defaulted: a register that cannot distinguish a lapse from a withdrawal
    /// answers none of the questions it exists for.
    /// </summary>
    public const string SupersedeReasonRequired = "CREDENTIAL_SUPERSEDE_REASON_REQUIRED";
}
