namespace Sankore.Modules.Kyc.Features.Files.ListKycFiles;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// The compliance dashboard's list (KYC-B-08). A query — no <c>ICommand</c>.
///
/// <para>
/// Until this existed the module could only be read by file id or by customer id, so no screen could
/// show "the files waiting on my branch" without already knowing which files to ask for.
/// </para>
/// </summary>
/// <param name="AgencyId">
/// Narrows WITHIN the caller's perimeter; it never widens it. An agency the caller cannot see
/// returns an empty page rather than a refusal — a refusal would confirm the agency exists.
/// </param>
/// <param name="From">
/// On <c>UpdatedAt</c>, not <c>CreatedAt</c>: the list is ordered by last activity and a period
/// filter on a dashboard means "what moved", not "what was opened".
/// </param>
internal sealed record ListKycFilesQuery(
    string? Status,
    Guid? AgencyId,
    string? VigilanceLevel,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize) : IRequest<Result<KycFileListPage>>;

/// <param name="AwaitingMeCount">
/// Files anywhere in the caller's perimeter whose next pending rung is one their ROLES can sign —
/// not only those on the current page. It is the dashboard badge, so it has to count the whole list.
/// </param>
public sealed record KycFileListPage(
    IReadOnlyList<KycFileListItem> Rows,
    int TotalCount,
    int Page,
    int PageSize,
    int AwaitingMeCount)
{
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 1;
}

/// <param name="CustomerId">
/// Opaque. The customer's NAME is deliberately not here: names live in clear in M01 so its search
/// stays an indexable prefix scan, and M01 exposes no batch lookup — resolving twenty names through
/// this module would be twenty cross-module calls per page, plus a copy of someone else's data to
/// keep in step. The screen resolves them from the clients API it already calls.
/// </param>
/// <param name="RequiredActionCode">
/// A stable code, not a sentence. Phrasing belongs to the screen, which already holds the module's
/// French labels; returning prose here would put one of the two languages this product speaks inside
/// a JSON contract.
/// </param>
/// <param name="AwaitingMe">
/// The next pending rung is one the caller's roles can sign. A HINT for sorting and highlighting —
/// never permission to sign. See <see cref="KycApproverLevels"/>: a stand-in holding a temporary
/// delegation but not the role is not highlighted here, and can still sign from the file itself.
/// </param>
public sealed record KycFileListItem(
    Guid KycFileId,
    Guid CustomerId,
    Guid? AgencyId,
    string Status,
    string Tier,
    string VigilanceLevel,
    int? ConfidenceScore,
    bool DuplicateSuspected,
    string RequiredActionCode,
    bool AwaitingMe,
    DateTimeOffset UpdatedAt);
