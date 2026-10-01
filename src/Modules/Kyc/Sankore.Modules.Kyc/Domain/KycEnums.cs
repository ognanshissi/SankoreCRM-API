namespace Sankore.Modules.Kyc.Domain;

using Sankore.Modules.Kyc.PublicApi;

/// <summary>
/// Lifecycle of a KYC file. Identifiers are English like every other enum in this codebase
/// (LeadStatus, ClientStatus, WorkflowTaskStatus); the French wording of the specification lives
/// in the resource strings an operator reads, never in a type name.
/// </summary>
public enum KycFileStatus
{
    /// <summary>EnCollecte — created, data being entered.</summary>
    Collecting,

    /// <summary>EnVérification — biometric calls running or queued.</summary>
    Verifying,

    /// <summary>EnValidation — in the approval circuit.</summary>
    Validating,

    /// <summary>ComplémentRequis — back to the agent with points to fix.</summary>
    ComplementRequired,

    /// <summary>Simplifié — simplified KYC approved, caps in force.</summary>
    Simplified,

    /// <summary>Complet — full KYC approved.</summary>
    Full,

    /// <summary>EnRevue — periodic or event-driven review under way.</summary>
    UnderReview,

    /// <summary>Expiré — review not done within the grace period.</summary>
    Expired,

    /// <summary>Rejeté — end of relationship.</summary>
    Rejected,

    /// <summary>Suspendu — compliance block.</summary>
    Suspended
}

public enum KycTier
{
    /// <summary>Not decided yet; the file has never been approved.</summary>
    None,
    Simplified,
    Full
}

/// <summary>How the customer was enrolled. Drives nothing on its own; it is evidence.</summary>
public enum KycChannel
{
    Agency,
    MobileAgent,
    Web,
    Import,
    LeadConversion
}

/// <summary>
/// Vigilance level required by the BCEAO graduated approach. It selects the approval circuit.
/// </summary>
public enum KycVigilanceLevel
{
    Low,
    Standard,
    High
}

public enum KycConfidenceLevel
{
    Rejected,
    Low,
    Medium,
    High
}

public static class KycStatusMapping
{
    /// <summary>
    /// Projects the ten internal statuses onto the six the public contract exposes.
    ///
    /// The contract predates this module and M01 already consumes it — its retention job and its
    /// anonymisation handler both call <c>IKycModule</c>. Changing the public enum would break
    /// them, so the richer model folds into it here, once, where the choice is visible:
    ///
    /// <list type="bullet">
    /// <item><c>UnderReview</c> → <c>Approved</c>: a review in progress does not un-validate a
    ///   customer. Reporting it as pending would freeze operations for a client who is in good
    ///   standing.</item>
    /// <item><c>Suspended</c> → <c>Rejected</c>: fail-closed. A downstream gate must treat a
    ///   compliance block exactly as it treats a refusal, never as "still being processed".</item>
    /// </list>
    /// </summary>
    public static KycStatus ToPublicStatus(this KycFileStatus status) => status switch
    {
        KycFileStatus.Collecting => KycStatus.Pending,

        KycFileStatus.Verifying
            or KycFileStatus.Validating
            or KycFileStatus.ComplementRequired => KycStatus.InProgress,

        KycFileStatus.Simplified
            or KycFileStatus.Full
            or KycFileStatus.UnderReview => KycStatus.Approved,

        KycFileStatus.Expired => KycStatus.Expired,

        KycFileStatus.Rejected or KycFileStatus.Suspended => KycStatus.Rejected,

        _ => KycStatus.NotStarted
    };
}
