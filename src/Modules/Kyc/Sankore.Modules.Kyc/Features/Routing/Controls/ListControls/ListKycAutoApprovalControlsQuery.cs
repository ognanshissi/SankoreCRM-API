namespace Sankore.Modules.Kyc.Features.Routing.Controls.ListControls;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The compliance queue of KRM-B-11: the automatic approvals that were drawn for control, and what
/// was found on the ones already looked at. A query — no <c>ICommand</c>.
/// </summary>
/// <param name="Status">
/// <c>Pending</c>, <c>Compliant</c>, <c>NonCompliant</c>, or null for all three. The screen opens
/// on Pending — that is the worklist — and the other two are how an officer shows a regulator what
/// has been controlled.
/// </param>
internal sealed record ListKycAutoApprovalControlsQuery(
    string? Status,
    int Page,
    int PageSize) : IRequest<Result<KycAutoApprovalControlPage>>;

/// <param name="PendingCount">
/// Everything still waiting in the tenant, not only on this page: it is the badge the compliance
/// screen carries, so it has to count the whole queue.
/// </param>
public sealed record KycAutoApprovalControlPage(
    IReadOnlyList<KycAutoApprovalControlItem> Rows,
    int TotalCount,
    int Page,
    int PageSize,
    int PendingCount)
{
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
}

/// <param name="CustomerName">
/// M01's <c>DisplayName</c>, resolved per page through one batch call and NOT stored in this
/// schema — a name copied here is a second source of truth that drifts on the next rename or
/// merge. Null when M01 no longer knows the id; the row still renders, because a compliance
/// worklist must not lose an item to a dangling reference.
/// </param>
/// <param name="PolicyVersion">
/// Which version of the automatic-approval conditions granted the tier. The question a regulator
/// asks is "under which rules was this relationship entered into without a human", and the answer
/// has to survive the rules changing.
/// </param>
/// <param name="RoutingReason">
/// Always null on these rows, and that is the point — a file reaches this queue only when NO
/// condition failed. It is in the contract so the screen can show the same shape it shows
/// everywhere else without a special case.
/// </param>
public sealed record KycAutoApprovalControlItem(
    Guid Id,
    Guid KycFileId,
    Guid CustomerId,
    string? CustomerName,
    string Status,
    string GrantedTier,
    int PolicyVersion,
    DateTimeOffset SampledAt,
    Guid? ReviewedBy,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? Finding,
    Guid? RaisedReviewId,
    string? RoutingReason = null);
