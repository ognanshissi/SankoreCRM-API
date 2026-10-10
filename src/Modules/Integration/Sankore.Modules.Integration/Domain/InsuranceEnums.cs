namespace Sankore.Modules.Integration.Domain;

/// <summary>
/// How a product's premium is obtained (ASS-03, criterion 2).
///
/// <para>
/// Two values and not a nullable amount, because "no amount stored" and "the insurer prices it"
/// are different configurations with different failure modes: the first is an unfinished
/// catalogue entry, the second is a complete one whose price simply is not ours to know. A
/// nullable amount would make the two indistinguishable and would offer a product with no price
/// at the counter.
/// </para>
/// </summary>
public enum ProductPricingMode
{
    /// <summary>The catalogue holds the premium. No insurer call is needed to quote.</summary>
    CatalogueFixed,

    /// <summary>
    /// The insurer computes it, through <c>IInsuranceProductPort.PriceAsync</c>. Offerable only
    /// while the connection's adapter declares <c>IntegrationCapability.PriceProduct</c> — which
    /// is why offerability is derived and never a stored flag.
    /// </summary>
    InsurerComputed
}

/// <summary>
/// Where a subscription stands as a whole (ASS-05, criterion 4: « l'état global de la
/// souscription est consultable »).
///
/// <para>
/// This exists because no single <c>integration_command</c> row knows it. ASS-05 splits the
/// subscription into three distinct commands chained by event — debit the premium, subscribe at
/// the insurer, and reverse the debit if the insurer refuses — and the status of the last one is
/// NOT the status of the whole: a <c>Rejected</c> reversal and a <c>Rejected</c> subscription are
/// the same command status and opposite business outcomes.
/// </para>
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>Created; the premium debit is queued. Nothing has reached the insurer.</summary>
    PendingDebit,

    /// <summary>
    /// The premium was debited and the CBS reference is recorded; the subscription is queued at
    /// the insurer. The only state in which money has moved and no policy exists yet.
    /// </summary>
    Debited,

    /// <summary>The insurer issued the policy. Terminal, and the only successful end.</summary>
    Issued,

    /// <summary>
    /// The debit was refused — insufficient funds, blocked account. Terminal, and the cheapest
    /// failure there is: nothing was sent to the insurer and no money moved (ASS-05, criterion 3).
    /// </summary>
    DebitFailed,

    /// <summary>The insurer refused; the reversal is queued.</summary>
    Reversing,

    /// <summary>The insurer refused and the customer has been refunded. Terminal, clean.</summary>
    Reversed,

    /// <summary>
    /// <b>The customer has been charged for a policy that does not exist.</b> The debit
    /// succeeded, the insurer refused, and the reversal failed too.
    ///
    /// <para>
    /// A named terminal state rather than an inference, and that is the point. "The last command
    /// is Rejected" describes this state AND a plain refused subscription that was correctly
    /// refunded, so a query written that way cannot list the money the institution owes. Here one
    /// predicate — <c>status = 'PremiumRefundDue'</c>, served by
    /// <c>ix_ins_subscription_tenant_status</c> — finds every one of them, and finding every one
    /// of them is a regulatory obligation, not an operational nicety.
    /// </para>
    ///
    /// <para>
    /// Not automatically retried into <see cref="Reversing"/> either: the reversal already
    /// consumed the dispatcher's retry budget, so a loop back would hide a failing CBS behind an
    /// endlessly "in progress" row. Re-attempting is a human decision, taken with the CBS
    /// reference that <c>ins_subscription.cbs_debit_reference</c> keeps for exactly that.
    /// </para>
    /// </summary>
    PremiumRefundDue,

    /// <summary>
    /// Abandoned by a human before the debit left. Terminal. Distinct from
    /// <see cref="DebitFailed"/> because nothing was refused — there is nothing to explain to the
    /// customer and nothing for an operator to act on.
    /// </summary>
    Cancelled
}

/// <summary>
/// One premium instalment's lifecycle (ASS-08).
///
/// <para>
/// <see cref="Failed"/> and <see cref="Unpaid"/> are deliberately two states. A failed attempt is
/// ours to retry under the product's policy; an <i>impayé</i> is the moment we stop retrying and
/// tell the insurer, which is what lets the insurer suspend the policy. Folding them would make
/// the retry budget and the declaration the same event.
/// </para>
/// </summary>
public enum InstalmentStatus
{
    /// <summary>Due in the future, or due now and not yet attempted.</summary>
    Scheduled,

    /// <summary>A debit command is in flight.</summary>
    Debiting,

    /// <summary>Debited. Feeds the statement of ASS-10.</summary>
    Paid,

    /// <summary>The debit failed and the product's retry budget is not spent.</summary>
    Failed,

    /// <summary>Retries exhausted and the <i>impayé</i> declared to the insurer.</summary>
    Unpaid,

    /// <summary>Written off by a human — a commercial gesture, or a correction.</summary>
    Waived,

    /// <summary>The policy ended before this instalment fell due.</summary>
    Cancelled
}

/// <summary>
/// How the customer's consent and signature were obtained (ASS-04, CIMA 2024).
///
/// <para>
/// Stored because the CIMA requirement is about the <i>form</i> of the consent, not only its
/// existence: a scanned paper signature and a one-time code sent to a phone are different
/// evidence and a control will ask which one a given contract has.
/// </para>
/// </summary>
public enum ConsentChannel
{
    /// <summary>Signed on paper at the counter; the scan is the evidence.</summary>
    PaperAtCounter,

    /// <summary>Signed on the agent's tablet.</summary>
    ElectronicSignaturePad,

    /// <summary>Accepted by a one-time code sent to the customer's own number.</summary>
    OneTimeCode,

    /// <summary>Accepted in the customer's own channel (portal, USSD, app).</summary>
    CustomerChannel
}

/// <summary>Outcome of the antivirus pass a claim document must survive (ASS-09, criterion 1).</summary>
public enum DocumentScanStatus
{
    /// <summary>Stored and not yet scanned. <b>Never transmitted in this state.</b></summary>
    Pending,

    /// <summary>Scanned, nothing found. The only state from which a document may be sent.</summary>
    Clean,

    /// <summary>
    /// Infected. The row stays — deleting it would lose the fact that somebody uploaded it — and
    /// the stored object is removed by the slice that owns the store.
    /// </summary>
    Infected,

    /// <summary>
    /// The scanner itself failed. Distinct from <see cref="Infected"/>: this is our outage, it is
    /// retryable, and it must not be reported to the agent as "your file is infected".
    /// </summary>
    ScanFailed
}

/// <summary>Lifecycle of a monthly insurer statement (ASS-10).</summary>
public enum StatementStatus
{
    /// <summary>
    /// Being built, and rebuildable: a regeneration deletes the lines and writes them again.
    /// The only state in which the figures may change.
    /// </summary>
    Draft,

    /// <summary>
    /// Closed. The figures are the ones the institution stands behind, and the lines are frozen —
    /// which is why a line carries its own copy of the policy number and the CBS reference rather
    /// than a join that would silently re-render last month's report from today's data.
    /// </summary>
    Finalised,

    /// <summary>Sent to the insurer through the adapter or the batch socle.</summary>
    Transmitted,

    /// <summary>Transmission refused or undeliverable. The statement itself is still valid.</summary>
    TransmitFailed
}

/// <summary>What one line of a statement accounts for (ASS-10, criterion 1).</summary>
public enum StatementLineType
{
    /// <summary>A new policy issued in the period — an <i>adhésion</i>.</summary>
    Subscription,

    /// <summary>A premium actually collected: the first one, or a periodic instalment.</summary>
    PremiumPaid,

    /// <summary>A premium given back — a <i>contre-passation</i>.</summary>
    Reversal,

    /// <summary>A policy cancelled or lapsed in the period.</summary>
    Cancellation
}
