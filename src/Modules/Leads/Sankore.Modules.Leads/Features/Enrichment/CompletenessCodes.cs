namespace Sankore.Modules.Leads.Features.Enrichment;

/// <summary>
/// The STABLE codes a completeness answer is made of (BNF-B-04: "codes stables").
///
/// <para>
/// Codes and not sentences: the front-end maps them to its own wording in its own language, and a
/// message that changed when somebody improved a label would break every client that branched on
/// it. Anything added here is permanent in the same way a public enum value is.
/// </para>
/// </summary>
public static class CompletenessCodes
{
    // ── The lead as a whole ──────────────────────────────────────────────

    /// <summary>
    /// No catalogue line is resolved, so nothing can be required of this lead at all — the policy
    /// that says what to collect is reached through the product line (§3.3). Reported FIRST and
    /// blocks the conversion: without it the most permissive path (a lead whose product nobody
    /// identified) would also be the easiest to convert.
    /// </summary>
    public const string ProductLineNotResolved = "PRODUCT_LINE_NOT_RESOLVED";

    /// <summary>A questionnaire a person still owes (BNF-B-08).</summary>
    public const string MissingQuestionnaire = "MISSING_QUESTIONNAIRE";

    // ── Per person ───────────────────────────────────────────────────────

    /// <summary>A field the product line requires before conversion, named by the policy.</summary>
    public const string MissingRequiredField = "MISSING_REQUIRED_FIELD";

    /// <summary>
    /// The person's PEP status has never been declared — neither exposed nor not. "Not asked" and
    /// "not exposed" are different answers and an LBC/FT file cannot treat them as one.
    /// </summary>
    public const string PepNotDeclared = "PEP_NOT_DECLARED";

    /// <summary>No active FATCA declaration for a person in scope (BNF-B-11).</summary>
    public const string FatcaDeclarationMissing = "FATCA_DECLARATION_MISSING";

    /// <summary>Indices declared and the documentation not yet produced.</summary>
    public const string FatcaDocumentationOutstanding = "FATCA_DOCUMENTATION_OUTSTANDING";

    // ── An existing customer's file must be up to date (BNF-B-23) ────────

    /// <summary>The client has no KYC file, or it has never been approved.</summary>
    public const string CustomerKycNotApproved = "CUSTOMER_KYC_NOT_APPROVED";

    /// <summary>The client's KYC has expired — valid once is not valid now.</summary>
    public const string CustomerKycExpired = "CUSTOMER_KYC_EXPIRED";

    /// <summary>
    /// The client's KYC was REFUSED. Distinct from "not approved yet" because it is not a delay —
    /// no amount of waiting closes it, and nothing should be sold to this client until compliance
    /// reopens the file.
    ///
    /// <para>
    /// Note what is NOT here: "under review". M02's public contract maps its internal
    /// <c>UnderReview</c> onto <c>Approved</c> on purpose — a review in progress does not
    /// un-validate a customer — so the state is invisible from this side and a code for it would
    /// never be produced.
    /// </para>
    /// </summary>
    public const string CustomerKycRejected = "CUSTOMER_KYC_REJECTED";

    /// <summary>The client record is archived, suspended or merged away — not somewhere to sell.</summary>
    public const string CustomerNotActive = "CUSTOMER_NOT_ACTIVE";

    /// <summary>The product line requires a KYC tier the client has not reached.</summary>
    public const string CustomerKycTierInsufficient = "CUSTOMER_KYC_TIER_INSUFFICIENT";

    /// <summary>The lead says it belongs to a client M01 does not have.</summary>
    public const string CustomerNotFound = "CUSTOMER_NOT_FOUND";

    // ── Degraded answers ─────────────────────────────────────────────────

    /// <summary>
    /// A module the check depends on could not be reached, so completeness is UNKNOWN rather than
    /// complete. Blocks the conversion: answering "complete" because M12 was down is the one
    /// failure mode a gate must not have.
    /// </summary>
    public const string CheckUnavailable = "COMPLETENESS_CHECK_UNAVAILABLE";
}
