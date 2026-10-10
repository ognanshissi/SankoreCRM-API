namespace Sankore.Modules.Integration.Features.Reconciliation;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// <b>What the daily comparisons can and cannot look for, and why (INT-34, BNF-B-17).</b>
///
/// <para>
/// There are now TWO comparisons with different inputs — customers against the CBS snapshot, and
/// insurance holdings against the policy read model — so the sets are per connection FAMILY. They
/// are kept in one class because the argument is the same in both cases and because the detected
/// set doubles as the auto-close filter: a run must close only what it was able to look for.
/// </para>
///
/// <para>
/// INT-34 names four gap types. Three of them have an input in this deployment; one does not, and
/// this class is the single place that says so — read by the job, stamped onto every run row
/// (<see cref="IntegrationReconciliationRun.UndetectableGapTypes"/>) and returned by the ledger
/// endpoint. A gap type that can never fire must not read like a gap type that fired and found
/// nothing: on a compliance report a zero is a measurement, and an absent measurement printed as
/// a zero is worse than no report.
/// </para>
///
/// <para>
/// ── Why <see cref="GapType.MissingInCrm"/> is not detectable ────────────────────────────
/// </para>
///
/// <para>
/// It means "the external system holds a record the CRM has no reference for". Detecting it needs
/// an <b>exhaustive enumeration of the external side</b>. Neither source this module has can
/// provide one:
/// </para>
///
/// <list type="bullet">
/// <item><c>cbs_customer_snapshot</c> (INT-21) is keyed by <c>crm_customer_id</c> and is written
///   only for customers we already hold a reference for — <c>CbsSnapshotProjector</c> returns
///   without writing when the reference lookup comes back empty. It is structurally incapable of
///   naming a customer the CRM does not know.</item>
/// <item>The inbound extraction (INT-25) sees only the lines a file happens to carry, and
///   <c>ExtractionApplier</c> reports an unknown external identifier as
///   <c>BATCH_CUSTOMER_NOT_REFERENCED</c> and persists nothing about it — on purpose: an unknown
///   identifier "is not an error, it is a customer this connection has never exchanged
///   with".</item>
/// </list>
///
/// <para>
/// <b>The rejected option was to widen INT-25's applier</b> and record those unknown identifiers
/// in a column or a side table so the reconciliation could report them. It was rejected on
/// correctness, not on cost. What it would detect is the subset of the external system's records
/// that happened to appear in a file during the window — a figure whose size is decided by the
/// partner's extraction scope, not by the divergence. An internal-control officer reads
/// "<c>MissingInCrm</c>: 3" as "three customers exist in the CBS and not in the CRM"; the true
/// figure, on an IMF that has not migrated its portfolio, is every customer it has ever had. A
/// biased subset presented as a count is a worse answer than an explicit refusal to count, and it
/// is the kind of wrong number nobody re-checks.
/// </para>
///
/// <para>
/// <b>And the story excludes the real question.</b> At scale <c>MissingInCrm</c> <i>is</i> the
/// un-imported CBS portfolio, which INT-34's own "hors périmètre" line puts outside this lot:
/// "l'import initial d'un portefeuille existant du CBS, qui reste une question ouverte". The
/// honest reading is that the gap type was written for a deployment that has an exhaustive
/// external extraction, and this one does not yet. When that import lands it will need a
/// full-portfolio enumeration port (an external customer list, paged) — and the day that port
/// exists, this class is the one place to move <see cref="GapType.MissingInCrm"/> from
/// <see cref="NotDetected"/> to <see cref="Detected"/>.
/// </para>
///
/// <para>
/// <b>A dangling reference is deliberately NOT reported as this type either.</b> When M01 answers
/// nothing for a referenced customer, the job counts it and logs a warning rather than opening a
/// gap. M01 never deletes a client (archiving is a status, anonymisation blanks fields), so the
/// case is a data-integrity anomaly rather than a reconciliation finding — and wiring it to
/// <c>MissingInCrm</c> would give that gap type a detector that in practice never fires, which is
/// the same lie in a different shape.
/// </para>
/// </summary>
internal static class ReconciliationScope
{
    /// <summary>
    /// What a CORE-BANKING run looks for. <b>It is also the auto-close filter</b>: a run may only
    /// close what it was able to look for, or the first run after a deployment would silently
    /// close every open gap of a type it never scanned.
    /// </summary>
    internal static readonly IReadOnlySet<GapType> Detected = new HashSet<GapType>
    {
        GapType.MissingInExternal,
        GapType.KycMismatch,
        GapType.StatusMismatch,
    };

    /// <summary>
    /// The types a core-banking run cannot look for. Stamped on the run row and surfaced by the
    /// ledger endpoint.
    ///
    /// <para>
    /// <see cref="GapType.MissingInCrm"/> for the reason argued at length above.
    /// <see cref="GapType.DesignationOutOfSync"/> is a question about a beneficiary clause, which
    /// a customer comparison has no business asking — the mirror of
    /// <see cref="GapType.KycMismatch"/> being absent from the insurance side. It is listed rather
    /// than ignored because the per-family guard test requires EVERY gap type to be accounted for
    /// by EVERY family: a type nobody declared would otherwise be a type one comparison might
    /// quietly auto-close without ever having looked for it.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyCollection<GapType> NotDetected =
        [GapType.MissingInCrm, GapType.DesignationOutOfSync];

    // ── Insurance holdings (BNF-B-17) ───────────────────────────────────────

    /// <summary>
    /// What an INSURANCE run looks for: a holding the insurer does not know, the two sides
    /// disagreeing about whether a policy is in force, and a clause the insurer was never sent.
    /// </summary>
    internal static readonly IReadOnlySet<GapType> DetectedForHoldings = new HashSet<GapType>
    {
        GapType.MissingInExternal,
        GapType.StatusMismatch,
        GapType.DesignationOutOfSync,
    };

    /// <summary>
    /// What an insurance run cannot look for, and why.
    ///
    /// <para>
    /// <see cref="GapType.MissingInCrm"/> — "the insurer holds a policy the CRM has no reference
    /// for" — needs an exhaustive enumeration of the insurer's portfolio. The only external source
    /// is <c>PolicyRecord</c>, which is written from what a subscription or a status push told us
    /// about policies we already hold a reference for: structurally incapable of naming a policy
    /// the CRM does not know. The same argument as the core-banking side, for the same reason.
    /// </para>
    ///
    /// <para>
    /// <see cref="GapType.KycMismatch"/> is not a question about a policy at all; it belongs to
    /// the customer comparison and would be a category error here.
    /// </para>
    ///
    /// <para>
    /// <b>And note what is NOT in this list.</b> A true clause comparison — "the insurer's
    /// beneficiaries differ from ours" — would need the insurer's own clause, and nothing in
    /// <c>PolicyRecord</c> records it. Rather than declare a gap type that can never fire, the
    /// detectable half of the question is reported instead:
    /// <see cref="GapType.DesignationOutOfSync"/>, which compares the CRM's clause version against
    /// the latest version this module actually succeeded in sending. That is a real divergence
    /// with a real remedy, and it is measured from data this module owns.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyCollection<GapType> NotDetectedForHoldings =
        [GapType.MissingInCrm, GapType.KycMismatch];

    /// <summary>The detected set for a connection family, so a run stamps and closes its own.</summary>
    internal static IReadOnlySet<GapType> DetectedFor(IntegrationFamily family)
        => family == IntegrationFamily.Insurance ? DetectedForHoldings : Detected;

    internal static IReadOnlyCollection<GapType> NotDetectedFor(IntegrationFamily family)
        => family == IntegrationFamily.Insurance ? NotDetectedForHoldings : NotDetected;
}
