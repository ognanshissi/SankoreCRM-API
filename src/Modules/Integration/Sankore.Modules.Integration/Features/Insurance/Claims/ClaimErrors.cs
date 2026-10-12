namespace Sankore.Modules.Integration.Features.Insurance.Claims;

/// <summary>
/// Error codes of the claims area. Stable UPPER_SNAKE, like every other code of the module.
/// </summary>
internal static class ClaimErrors
{
    public const string PolicyNotFound = "INSURANCE_POLICY_NOT_FOUND";

    public const string ClaimNotFound = "INSURANCE_CLAIM_NOT_FOUND";

    /// <summary>
    /// The loss did not happen during the cover. Suffixed with <c>:BEFORE_COVER</c> or
    /// <c>:AFTER_COVER</c> so the agent is told which end of the window refused it — a claim
    /// rejected with no side named sends them to read the contract rather than the date they
    /// mistyped.
    /// </summary>
    public const string LossOutsideCover = "INSURANCE_LOSS_OUTSIDE_COVER";

    /// <summary>
    /// A loss dated in the future. Its own code and not a validator rule on the request: "today"
    /// is the platform's clock and a validator has no business holding one.
    /// </summary>
    public const string LossInTheFuture = "INSURANCE_LOSS_IN_THE_FUTURE";

    /// <summary>
    /// The insurer declares no <c>DeclareClaim</c> capability — a bordereau-fed insurer whose
    /// claims are declared on paper. A conflict and not a 400: the request is well-formed and the
    /// configuration is what refuses it.
    /// </summary>
    public const string InsurerCannotTakeClaims = "INSURANCE_CONNECTION_CANNOT_DECLARE_CLAIMS";

    /// <summary>
    /// An indemnity recorded on a claim the insurer refused, or on one already paid with a
    /// different reference. Both mean two systems disagree about money, and picking a side
    /// silently is what a reconciliation cannot undo.
    /// </summary>
    public const string IndemnityConflict = "INSURANCE_CLAIM_INDEMNITY_CONFLICT";
}
