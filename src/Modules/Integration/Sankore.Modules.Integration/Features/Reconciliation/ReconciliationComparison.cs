namespace Sankore.Modules.Integration.Features.Reconciliation;

using System.Text.Json;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Snapshot;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The comparison rules of INT-34, as pure functions — so the decisions are pinned by tests
/// without a job, a container or a database, the split every sweep in this module uses.
/// </summary>
internal static class ReconciliationComparison
{
    /// <summary>
    /// <b>The one definition of "active in the CRM" this module uses.</b>
    ///
    /// <para>
    /// M01 exposes two readings of it and they are not interchangeable:
    /// <c>ICustomersModule.ExistsAndActiveAsync</c>, documented as "exists AND its status is
    /// Active — i.e. KYC has been approved and the record is neither pending, suspended,
    /// rejected, archived nor merged", and <c>ClientSummary.Status</c>, the raw
    /// <c>ClientStatus</c> name. This method reads the SECOND and reproduces the FIRST's rule
    /// exactly, because <c>GetClientSummariesAsync</c> answers a whole page in one query while
    /// <c>ExistsAndActiveAsync</c> is one round trip per customer — a reconciliation of fifty
    /// thousand references cannot afford fifty thousand of them.
    /// </para>
    ///
    /// <para>
    /// Ordinal comparison against the enum name, not case-insensitive: the string is produced by
    /// <c>ClientStatus.ToString()</c> on the other side of the contract, so a case difference
    /// would mean M01 changed how it serialises its own enum — which must surface as a failing
    /// test here, not be absorbed.
    /// </para>
    /// </summary>
    internal static bool IsActiveInCrm(ClientSummary summary)
        => string.Equals(summary.Status, "Active", StringComparison.Ordinal);

    /// <summary>
    /// The divergence for one referenced customer, or <c>null</c> when the two sides agree.
    ///
    /// <para>
    /// <paramref name="snapshotExists"/> is the external side's whole answer about existence, and
    /// that is INT-21's own convention rather than an inference of this slice:
    /// <c>CbsSnapshotProjector</c> writes nothing at all for a customer the CBS does not hold —
    /// "the absence of the row IS the 'does not exist in the CBS' answer" — precisely so a reader
    /// is not shown a row of zeros.
    /// </para>
    ///
    /// <para>
    /// <b>The order of the branches is the precedence, and it is deliberate.</b> A customer the
    /// CRM has suspended or merged while the CBS still carries it is reported as
    /// <see cref="GapType.StatusMismatch"/> and its KYC tier is not compared at all. One customer
    /// yields at most one gap: reporting both would count the same person twice in the summary an
    /// administrator reads, and the remedy for a suspended customer is the status, not the tier —
    /// a tier divergence on a record that should not be operable is noise in front of the finding
    /// that matters.
    /// </para>
    /// </summary>
    /// <param name="crmKycLevel">
    /// The tier M02 believes, already translated by <c>SnapshotKycLevels.FromLimits</c>. Null when
    /// M02 could not be asked — the KYC comparison is then skipped rather than guessed, the same
    /// direction <c>KycLimitWatchJob</c> takes: "we could not find out" is not a divergence.
    /// </param>
    internal static ReconciliationDivergence? Compare(
        ClientSummary summary,
        bool snapshotExists,
        KycLevel? crmKycLevel,
        KycLevel? cbsKycLevel)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var activeInCrm = IsActiveInCrm(summary);

        if (!snapshotExists)
        {
            // Active here and absent there: the CRM holds a reference the external system does
            // not know. This is the type that catches a customer created in the CRM whose CBS
            // write was lost, which is what the whole story is for.
            if (activeInCrm)
            {
                return new ReconciliationDivergence(
                    GapType.MissingInExternal,
                    Details(new Dictionary<string, object?>
                    {
                        ["crmStatus"] = summary.Status,
                        ["presentInExternal"] = false,
                    }));
            }

            // Inactive on BOTH sides is agreement, not a gap. An archived customer the CBS no
            // longer carries is the system working. Opening a gap here would fill the ledger with
            // every customer either side has ever retired.
            return null;
        }

        if (!activeInCrm)
        {
            // Both sides know the customer and disagree about whether it may operate: the CRM has
            // taken it out of service and the external system still carries it.
            //
            // A MERGED client lands here on purpose rather than by accident. M01's merge leaves
            // the absorbed record at status `Merged` with `MergedIntoId` set, and this module's
            // reference still points at the absorbed id — so the external system is addressing a
            // record the CRM has retired. That IS a status divergence, and the `merged` flag is in
            // the details so the operator can see that the remedy is to re-point the reference at
            // the survivor rather than to reactivate anything. Nothing here re-points it: INT-34
            // reports and never corrects, the same stance INT-21 takes on a KYC tier, and a
            // reference is the bridge between a customer and a bank account.
            return new ReconciliationDivergence(
                GapType.StatusMismatch,
                Details(new Dictionary<string, object?>
                {
                    ["crmStatus"] = summary.Status,
                    ["presentInExternal"] = true,
                    ["merged"] = summary.MergedIntoId is not null,
                }));
        }

        // ── The KYC tier ────────────────────────────────────────────────────
        // Decided by SnapshotKycLevels and not re-derived here: that file is where M02's
        // vocabulary becomes this module's, and it already owns the judgement that an UNKNOWN
        // external tier is not a divergence (the CBS has simply never been told, and reporting
        // that would queue every customer created before a tier was ever pushed). A second
        // definition of a tier comparison is exactly what it exists to prevent.
        if (crmKycLevel is null) return null;

        if (!SnapshotKycLevels.Diverge(crmKycLevel.Value, cbsKycLevel)) return null;

        return new ReconciliationDivergence(
            GapType.KycMismatch,
            Details(new Dictionary<string, object?>
            {
                ["crmKycLevel"] = crmKycLevel.Value.ToString(),
                ["externalKycLevel"] = cbsKycLevel!.Value.ToString(),
            }));
    }

    /// <summary>
    /// The gap's <c>details</c> jsonb.
    ///
    /// <para>
    /// <b>Compared VALUES only — never identity data.</b> The aggregate's own remark is the
    /// reason: a reconciliation report is routinely exported and mailed, so a status name, an
    /// active flag and a KYC tier may travel in it while a name, a document number or a phone
    /// number may not. Note what is absent even though it was to hand: the external identifier.
    /// It is an identifier of a person at a bank, and it is recoverable from the gap's
    /// <c>crm_id</c> through <c>GET integration/references</c> by anyone who holds the permission.
    /// </para>
    /// </summary>
    // ── Insurance holdings (BNF-B-17) ───────────────────────────────────────

    /// <summary>
    /// The divergence for one referenced HOLDING, or <c>null</c> when the two sides agree.
    /// </summary>
    /// <param name="crm">
    /// What M01 believes, from the batched state read. <c>null</c> when the CRM has no such
    /// holding — a reference pointing at nothing.
    /// </param>
    /// <param name="policyIsLive">
    /// The insurer's answer, from <c>PolicyRecord.IsLive</c>. <c>null</c> when there is no policy
    /// record at all, which is the read model's own convention for "the insurer never told us
    /// about this one" — the same shape as the CBS snapshot's absence.
    /// </param>
    /// <param name="currentClauseSent">
    /// Whether a <c>SubscribePolicy</c> command for the holding's CURRENT clause version has
    /// succeeded.
    ///
    /// <para>
    /// A boolean and not a "highest version sent", deliberately. The caller answers it by
    /// recomputing the deterministic idempotency key for exactly that (holding, version) and
    /// looking for a succeeded command — an EXACT lookup. Counting succeeded commands instead
    /// would under-report whenever an intermediate avenant was rejected and a later one landed,
    /// which is a false finding on a contract that is in fact up to date; this module's own
    /// scope class argues at length that a figure measuring something other than what its label
    /// says is worse than no figure.
    /// </para>
    /// </param>
    /// <remarks>
    /// <b>The order of the branches is the precedence, and it is deliberate</b> — the same
    /// reasoning as the customer comparison. One holding yields at most one gap: a holding the
    /// insurer does not know is reported as missing and its clause version is not compared,
    /// because the remedy is the subscription and not the avenant. Reporting both would count one
    /// contract twice in the summary an administrator reads.
    /// </remarks>
    internal static ReconciliationDivergence? CompareHolding(
        HoldingSyncState? crm,
        bool? policyIsLive,
        bool currentClauseSent)
    {
        // A reference pointing at no holding. NOT reported as MissingInCrm, for the reason the
        // scope class gives about its core-banking twin: M01 never deletes a holding (closing is a
        // status), so this is a data-integrity anomaly rather than a reconciliation finding, and
        // wiring it to a gap type would give that type a detector that in practice never fires.
        if (crm is null)
            return null;

        if (policyIsLive is null)
        {
            // The CRM believes the contract is in force and the insurer has never confirmed it.
            // This is the type that catches a subscription whose command was lost, which is what
            // the whole story is for.
            if (crm.IsLive)
            {
                return new ReconciliationDivergence(
                    GapType.MissingInExternal,
                    Details(new Dictionary<string, object?>
                    {
                        ["crmStatus"] = crm.Status,
                        ["crmExternalRef"] = crm.ExternalRef,
                        ["presentInExternal"] = false,
                    }));
            }

            // Not live here and unknown there: the two sides agree that nothing is in force. A
            // closed or rejected holding whose policy record was never written is the ordinary
            // end of a subscription that never happened, not a divergence.
            return null;
        }

        if (crm.IsLive != policyIsLive)
        {
            return new ReconciliationDivergence(
                GapType.StatusMismatch,
                Details(new Dictionary<string, object?>
                {
                    ["crmStatus"] = crm.Status,
                    ["liveInCrm"] = crm.IsLive,
                    ["liveInExternal"] = policyIsLive,
                }));
        }

        // Both agree the contract is in force, and the insurer is working from a clause we have
        // since replaced. An avenant recorded while the insurer was unreachable, or one whose
        // command was rejected and never re-sent, lands here — and it matters: the policy would
        // pay the previous beneficiaries.
        //
        // Only for a LIVE holding. A closed one whose last avenant never left is history, and
        // reporting it would fill the ledger with findings that have no remedy.
        if (crm.IsLive && !currentClauseSent)
        {
            return new ReconciliationDivergence(
                GapType.DesignationOutOfSync,
                Details(new Dictionary<string, object?>
                {
                    ["crmClauseVersion"] = crm.ClauseVersion,
                    ["currentClauseSent"] = false,
                }));
        }

        return null;
    }

    private static string Details(Dictionary<string, object?> values)
        => JsonSerializer.Serialize(values, DetailsJsonOptions);

    private static readonly JsonSerializerOptions DetailsJsonOptions = new()
    {
        WriteIndented = false,
    };
}

/// <summary>One divergence, ready to become a gap row.</summary>
internal sealed record ReconciliationDivergence(GapType GapType, string DetailsJson);
