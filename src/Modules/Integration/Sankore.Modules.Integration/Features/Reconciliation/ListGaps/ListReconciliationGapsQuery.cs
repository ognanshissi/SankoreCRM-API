namespace Sankore.Modules.Integration.Features.Reconciliation.ListGaps;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The reconciliation ledger, paginated (INT-34). A query — no <c>ICommand</c>, so it is neither
/// transacted nor audited: reading a list of findings is not itself a mutation, and the audit
/// table is kept meaningful by holding only those.
///
/// <para>
/// <c>Integration.Reconciliation.View</c> existed in <c>Permissions.All</c> with nothing behind
/// it. A compliance ledger nobody can read is not a ledger: the nightly job would record findings
/// and the only way to see one would be a <c>psql</c> session.
/// </para>
/// </summary>
/// <param name="Resolution">
/// <c>Open</c>, <c>Resolved</c> or <c>Closed</c>. <b>Defaults to <c>Open</c></b> — the question
/// this screen answers is "what is outstanding", and an unfiltered list is dominated by history
/// within weeks. Unparsable values are refused by name rather than ignored: silently dropping the
/// filter would answer with every gap ever recorded, which reads as a catastrophic morning.
/// </param>
/// <param name="GapType">
/// One of the four <c>GapType</c> names. Unparsable values are likewise refused rather than
/// ignored.
/// </param>
internal sealed record ListReconciliationGapsQuery(
    Guid? ConnectionId,
    string? GapType,
    string? Resolution,
    int Page,
    int PageSize) : IRequest<Result<ReconciliationGapPage>>;

/// <param name="OpenCountsByType">
/// How many gaps are OPEN right now, per type, <b>regardless of the page or of the resolution
/// filter</b> — this is the headline figure, and it must not change when somebody turns a page.
/// Every type the comparison can look for appears, with a zero when there are none, so a type is
/// never missing merely because it is clean.
/// </param>
/// <param name="UndetectableGapTypes">
/// Gap types no source in this deployment can feed, so no count for them can ever be anything but
/// zero. Returned next to the counts precisely so a reader does not take their absence from
/// <paramref name="OpenCountsByType"/> as "none found" — see <c>ReconciliationScope</c> for the
/// argument, and <c>MissingInCrm</c> for the case.
/// </param>
/// <param name="LastRun">
/// The most recent comparison of the scope asked for, or null if none has run. It is what makes
/// the page honest about itself: an empty ledger under a run that FAILED last night means
/// something very different from an empty ledger under a run that finished.
/// </param>
public sealed record ReconciliationGapPage(
    IReadOnlyList<ReconciliationGapRow> Rows,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyDictionary<string, int> OpenCountsByType,
    IReadOnlyList<string> UndetectableGapTypes,
    ReconciliationRunSummary? LastRun)
{
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
}

/// <summary>
/// One recorded divergence.
/// </summary>
/// <param name="CrmId">
/// The CRM customer the gap is about. Null for <c>MissingInCrm</c>, which points at no CRM row.
/// </param>
/// <param name="ExternalId">
/// Null for <c>MissingInExternal</c>: that gap exists because the external system does not hold
/// the record, and the identifier we assigned it is recoverable through
/// <c>GET integration/references</c>.
/// </param>
/// <param name="Details">
/// The compared values, as a compact JSON object — a KYC tier, a client status, an active flag.
/// A string rather than a typed object because the shape differs per gap type and the column is
/// jsonb; it carries no identity data by construction, which is what makes this response safe to
/// export.
/// </param>
/// <param name="DetectedAt">
/// When the divergence was FIRST seen. Unchanged by later runs — it is the age of the problem,
/// which is the figure an inspection asks for.
/// </param>
/// <param name="LastSeenAt">The last comparison that still found it.</param>
public sealed record ReconciliationGapRow(
    Guid GapId,
    Guid RunId,
    Guid ConnectionId,
    string GapType,
    Guid? CrmId,
    string? ExternalId,
    string? Details,
    string Resolution,
    DateTimeOffset DetectedAt,
    DateTimeOffset LastSeenAt,
    Guid? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

/// <param name="GapCount">Divergences the run observed: the ones it opened plus the ones it touched.</param>
/// <param name="FailureDetail">
/// Set when the comparison was abandoned. A run with a null <c>FinishedAt</c> is still in flight;
/// one with a <c>FinishedAt</c> and a <c>FailureDetail</c> crashed, which is itself a finding.
/// </param>
public sealed record ReconciliationRunSummary(
    Guid RunId,
    Guid ConnectionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int CheckedCount,
    int GapCount,
    int ClosedCount,
    string? FailureDetail);
