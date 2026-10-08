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
/// Opaque — an id into M01, never a foreign key.
/// </param>
/// <param name="CustomerName">
/// M01's <c>DisplayName</c>, resolved for the page's rows through one batch call
/// (<c>ICustomersModule.GetClientSummariesAsync</c>) and NOT stored here: a name copied into the
/// kyc schema is a second source of truth that drifts on the next rename or merge.
///
/// <para>
/// <c>null</c> when M01 does not know the id — a dangling reference left by a purge, or a tenant
/// mismatch. A compliance worklist must still render the row and its required action; the screen
/// shows a placeholder. That is also why resolution is read-only and never fails the page.
/// </para>
///
/// <para>
/// Safe to return in clear: M01 keeps names unencrypted precisely so its own search stays an
/// indexable prefix scan, so this leaks nothing the clients API would not already answer to the
/// same caller — unlike a phone or document number, which only the audited reveal endpoint hands
/// out.
/// </para>
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
    string? CustomerName,
    Guid? AgencyId,
    string Status,
    string Tier,
    string VigilanceLevel,
    int? ConfidenceScore,
    bool DuplicateSuspected,
    string RequiredActionCode,
    bool AwaitingMe,
    DateTimeOffset UpdatedAt);
