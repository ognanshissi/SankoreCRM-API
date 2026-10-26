namespace Sankore.Modules.Customers.Domain;

/// <summary>
/// Where a holding came from, and therefore whether the CRM may change it (BNF-B-14).
/// </summary>
public enum HoldingOrigin
{
    /// <summary>
    /// Subscribed through this CRM. The only origin the write endpoints accept.
    /// </summary>
    CrmSubscription,

    /// <summary>
    /// Projected from a back-office system the client already held the product in — a
    /// <c>PolicyRecord</c> or a CBS snapshot, pushed by an Integration consumer.
    ///
    /// <para>
    /// <b>READ-ONLY</b>, and the refusal is in the aggregate rather than in a handler: the
    /// authoritative copy lives in the other system, so a CRM edit would be silently overwritten by
    /// the next reconciliation run and the agent would never learn their change was lost.
    /// </para>
    /// </summary>
    ImportedFromBackOffice
}

/// <summary>
/// The life of a holding (BNF-B-14). Linear until it is live, then it can only close.
///
/// <para>
/// The transitions are declared in <c>ProductHolding.Allowed</c>, not compared by handlers — the
/// same shape as M02's <c>KycFile</c>, which learned that a status comparison spread across slices
/// is a status machine nobody can read.
/// </para>
/// </summary>
public enum HoldingStatus
{
    /// <summary>Being captured. Everything is editable, nothing has been promised.</summary>
    Draft,

    /// <summary>
    /// Complete and waiting for the client's signature. BNF-B-16 makes the signature the gate
    /// before anything is sent to a back office.
    /// </summary>
    PendingSignature,

    /// <summary>Signed and queued for the back office. Nothing editable but a cancellation.</summary>
    PendingSync,

    /// <summary>The back office accepted it. <c>ExternalRef</c> is set.</summary>
    Active,

    /// <summary>The back office refused it, with a reason an agent can act on.</summary>
    Rejected,

    /// <summary>Ended — surrendered, matured, cancelled before signature.</summary>
    Closed,

    /// <summary>
    /// Accepted by the insurer, premium not yet paid (insurance lot 2): the insurer collects it, or
    /// the 60-day derogation applies. <c>ExternalRef</c> is set; the holding becomes Active once the
    /// premium is confirmed — never before.
    /// </summary>
    AwaitingPayment
}

/// <summary>
/// What a designation role means for a holding (BNF-B-15). The CODES are per product line and
/// configured by the administrator (<c>DesignationRoleDefinition</c>); this is only the status of
/// one designation.
/// </summary>
public enum DesignationStatus
{
    Active,

    /// <summary>
    /// The designated person is no longer in the household, so the designation needs a human
    /// decision (BNF-B-13). It is NOT deleted: a household that forgets a separation cannot answer
    /// who was covered when a claim was filed.
    /// </summary>
    ToReview,

    /// <summary>Superseded by a later version of the same designation, or withdrawn.</summary>
    Closed
}
