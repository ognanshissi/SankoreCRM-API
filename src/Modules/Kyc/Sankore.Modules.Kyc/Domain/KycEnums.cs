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
/// <summary>
/// The kind of identity document being read, as DECLARED by the agent at capture.
///
/// <para>
/// It is an input and not a reading: <c>POST /v1/ocr</c> requires it, because the extractor uses a
/// different field template per document. The OCR answer carries a type too — what the service
/// believes it read — and that is the one persisted on the evidence; this one only says what the
/// agent put in front of the camera.
/// </para>
///
/// <para>The four the biometric service supports; its enum is the authority on this list.</para>
/// </summary>
public enum KycDocumentType
{
    /// <summary>Carte nationale d'identité.</summary>
    Cni,
    Passport,
    /// <summary>Carte d'identité biométrique CEDEAO/ECOWAS.</summary>
    Cedeao,
    /// <summary>Carte consulaire.</summary>
    Consulaire,
}

public enum KycChannel
{
    Agency,
    MobileAgent,
    Web,
    Import,
    LeadConversion,

    /// <summary>
    /// The customer sent their own documents from their own phone, through a single-use link
    /// (KRM-B-07).
    ///
    /// <para>
    /// Distinct from <see cref="Web"/>, which is an agent working from a browser, and it is not a
    /// cosmetic distinction: this is the one channel where <b>no employee ever saw the document</b>.
    /// It is what tells a validator what they are looking at, it is what CIMA LBC/FT article 13.6
    /// keys its proof-of-address requirement on (see <c>RemoteSaleEvidence</c>), and it is what the
    /// scorer is told so a remote capture is not graded like one taken across a counter.
    /// </para>
    ///
    /// <para>
    /// Set at SUBMISSION and not when the link is sent. Until the customer actually uploads, the
    /// file is still whatever opened it — usually <see cref="LeadConversion"/> — because a link
    /// nobody used is not a remote enrolment, it is a file waiting at a counter. The agent's screens
    /// key their "à distance" badge on the invitation instead, so a file still waiting on its
    /// customer is not mislabelled either way.
    /// </para>
    /// </summary>
    RemoteSelfService
}

/// <summary>
/// Which enrolment channels count as a REMOTE sale.
///
/// <para>
/// Spelled once because two unrelated rules key on it — CIMA LBC/FT article 13.6's proof of
/// address (<c>RemoteSaleEvidence</c>) and the scorer's weighting — and adding
/// <see cref="KycChannel.RemoteSelfService"/> showed exactly what a per-call enumeration costs: the
/// newest and most remote channel of all would have fallen outside a list written as
/// <c>Web or MobileAgent</c>, and the proof-of-address requirement would have stopped applying to
/// the only channel where no employee ever saw the document. The failure would have been silent and
/// in the customer's favour, which is the worst direction for a compliance gate.
/// </para>
/// </summary>
public static class KycChannels
{
    /// <summary>
    /// True when nobody from the institution was physically in front of the customer.
    ///
    /// <para>
    /// <see cref="KycChannel.MobileAgent"/> IS here even though an agent was present: the CIMA
    /// text is about a relationship entered into away from the branch, and that is the reading the
    /// product owner adopted when the rule was first written. <see cref="KycChannel.Import"/> and
    /// <see cref="KycChannel.LeadConversion"/> are NOT: an import is a migration of files collected
    /// some other way, and a conversion is a file still waiting to be collected — neither is a sale
    /// concluded at a distance, and requiring a utility bill of them would block a data migration.
    /// </para>
    /// </summary>
    public static bool IsRemoteSale(KycChannel channel)
        => channel is KycChannel.Web
                   or KycChannel.MobileAgent
                   or KycChannel.RemoteSelfService;
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

/// <summary>
/// Who decided — or is going to decide — a remotely collected file (KRM-B-08).
///
/// <para>
/// Recorded on the file rather than inferred from the circuit, because the two answers must be
/// readable apart. A file with a single <see cref="KycApprovalLevel.Automatic"/> step and a file
/// an agent happened to approve alone both show "one signature"; only this column says that no
/// human was ever asked. A regulator's question is "how many of your relationships were entered
/// into without a human decision", and it has to be a predicate, not a reconstruction.
/// </para>
/// </summary>
public enum KycValidationRoute
{
    /// <summary>A human decides, through the ordinary M02 circuit.</summary>
    Manual,

    /// <summary>
    /// The product's policy approved it with no human at all. Written only by
    /// <see cref="KycFile.ApproveAutomatically"/>, and only after every condition of the
    /// specification's section 3.2 was met.
    /// </summary>
    Automatic
}

/// <summary>
/// WHY a remotely collected file was sent to a human (KRM-B-08), as a stable code.
///
/// <para>
/// A code and not a sentence: it is shown to the agent translated by the front end, it is counted
/// in compliance reporting ("how often does the automatic mode decline, and on what"), and it is
/// stored. A French sentence would make all three depend on the wording.
/// </para>
///
/// <para>
/// <b>The automatic mode never rejects.</b> Every one of these is "a human looks at it", never "the
/// customer is refused" — which is why there is no code for a bad score alone that closes a file.
/// </para>
/// </summary>
public static class KycRoutingReasons
{
    /// <summary>The product itself asks for a human, or has no remote policy at all.</summary>
    public const string ProductPolicy = "PRODUCT_POLICY";

    /// <summary>
    /// The tenant has withdrawn the authorisation to approve without a human
    /// (<c>KYC_AUTO_APPROVAL_ENABLED</c>).
    ///
    /// <para>
    /// <b>Not in the specification's list, and it has to exist.</b> KRM-B-01 forbids SAVING a
    /// product in automatic mode while the tenant switch is off, so this state is only reachable by
    /// switching it off AFTERWARDS — which a compliance officer is entitled to do, and which is
    /// exactly the moment they need to see that it took effect. The nearest listed code,
    /// <see cref="TierAboveAutoLimit"/>, would tell them the product asks for too high a tier,
    /// which is not what happened and would send them to edit the wrong screen.
    /// </para>
    /// </summary>
    public const string AutoApprovalDisabled = "AUTO_APPROVAL_DISABLED";

    /// <summary>The product asks for a tier above the tenant's ceiling for an unattended grant.</summary>
    public const string TierAboveAutoLimit = "TIER_ABOVE_AUTO_LIMIT";

    /// <summary>
    /// The confidence is not high enough: the level is below <c>High</c>, or the global score is
    /// under the product's <c>MinAutoScore</c>. A <c>Rejected</c> level lands here too — the file
    /// goes to an agent who decides between a complement and a refusal.
    /// </summary>
    public const string ScoreBelowThreshold = "SCORE_BELOW_THRESHOLD";

    /// <summary>The scoring service raised one of <see cref="KycBlockingFlags"/>.</summary>
    public const string BlockingFlag = "BLOCKING_FLAG";

    /// <summary>What was read on the document does not agree with the client record.</summary>
    public const string IdentityMismatch = "IDENTITY_MISMATCH";

    /// <summary>A politically exposed person. The general management authorises, never a machine.</summary>
    public const string Pep = "PEP";

    /// <summary>High vigilance, or an open event-driven review on the file.</summary>
    public const string HighVigilance = "HIGH_VIGILANCE";

    /// <summary>The faces matched, but not on the first attempt — or did not match at all.</summary>
    public const string FaceRetry = "FACE_RETRY";

    /// <summary>A document the product requires is not on file.</summary>
    public const string MissingDocument = "MISSING_DOCUMENT";

    /// <summary>
    /// The customer exhausted their photo budget on at least one document. The upload was accepted
    /// anyway (KRM-B-06 makes it a budget, not a wall) and this is the other half of that choice:
    /// somebody looks at what was finally sent.
    /// </summary>
    public const string QualityRetriesExceeded = "QUALITY_RETRIES_EXCEEDED";

    /// <summary>
    /// CIMA LBC/FT article 13.6's proof of address is not on file in an acceptable state.
    ///
    /// <para>
    /// <b>The second code the specification's list does not have, and the more consequential
    /// one.</b> Section 3.2 never mentions the proof of address, but <c>RemoteSaleEvidence</c>
    /// refuses the FINAL APPROVAL of any remote file without one — and
    /// <see cref="KycChannel.RemoteSelfService"/> is the most remote channel there is. An automatic
    /// approval that skipped that gate would grant a tier the manual path would have refused, which
    /// is the one way this feature could be worse than no feature. So the gate applies to both
    /// paths, and a file that fails it routes to a human under this code.
    /// </para>
    ///
    /// <para>
    /// It is NOT folded into <see cref="MissingDocument"/>, although both say "a document is
    /// missing". That one is about the product's own list and is satisfied by an upload; this one
    /// needs the proof ACCEPTED by a reviewer and carrying an issue date — so an agent reading
    /// "pièce manquante" would tell the customer to send a photograph they have already sent. The
    /// operational consequence is documented on <c>KycAutoApprovalPolicy.Evaluate</c>.
    /// </para>
    /// </summary>
    public const string ProofOfAddressRequired = "PROOF_OF_ADDRESS_REQUIRED";
}

/// <summary>
/// The flags that forbid an unattended approval, as the specification's section 3.2 names them.
///
/// <para>
/// <b>They are the scoring service's own vocabulary, not this module's.</b>
/// <c>RunKycVerificationHandler</c> stores <c>ConfidenceScore.Flags</c> verbatim, so these are
/// matched as strings and case-insensitively rather than parsed into an enum — a service that
/// renames one must not make this gate throw, and a service that adds one must not make it pass.
/// A flag this list does not know is NOT blocking: the list is the compliance decision, and
/// widening it is a decision somebody takes, not something a service deployment does.
/// </para>
/// </summary>
public static class KycBlockingFlags
{
    public const string MrzChecksumFailed = "MRZ_CHECKSUM_FAILED";
    public const string VisualMrzMismatch = "VISUAL_MRZ_MISMATCH";
    public const string DocumentExpired = "DOCUMENT_EXPIRED";
    public const string FaceMismatch = "FACE_MISMATCH";
    public const string DuplicateDocumentSuspected = "DUPLICATE_DOCUMENT_SUSPECTED";

    private static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        MrzChecksumFailed, VisualMrzMismatch, DocumentExpired, FaceMismatch,
        DuplicateDocumentSuspected,
    };

    /// <summary>
    /// The first blocking flag in <paramref name="flags"/>, or null when none of them blocks.
    /// Returned rather than a boolean so the routing reason can be logged with the flag that
    /// caused it — without putting it in the reason code, which is counted.
    /// </summary>
    public static string? FirstBlocking(IEnumerable<string>? flags)
        => flags?.FirstOrDefault(f => f is not null && All.Contains(f.Trim()));
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
