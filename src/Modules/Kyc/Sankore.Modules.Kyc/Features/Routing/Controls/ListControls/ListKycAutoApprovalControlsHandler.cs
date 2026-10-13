namespace Sankore.Modules.Kyc.Features.Routing.Controls.ListControls;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads one page of the compliance queue.
///
/// <para>
/// <b>No agency perimeter, unlike every other list in this module.</b> It is a deliberate
/// exception and the permission is what makes it safe: <c>kyc:auto-approval:control</c> is held by
/// a compliance officer whose job is the institution's whole exposure, and a queue narrowed to one
/// branch would answer a question nobody asked — "did MY branch let machines decide" — while
/// leaving the real one, about the tenant, unanswerable. The rows themselves carry no identity
/// beyond a display name M01 would give the same caller anyway.
/// </para>
/// </summary>
internal sealed class ListKycAutoApprovalControlsHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    ICustomersModule customers,
    IAdministrationModule administration)
    : IRequestHandler<ListKycAutoApprovalControlsQuery, Result<KycAutoApprovalControlPage>>
{
    private const int MaxPageSize = 100;

    public async Task<Result<KycAutoApprovalControlPage>> Handle(
        ListKycAutoApprovalControlsQuery request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 25 : request.PageSize, 1, MaxPageSize);

        // The global query filter scopes this to the caller's tenant.
        var query = db.KycAutoApprovalControls.AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<KycAutoApprovalControlStatus>(
                    request.Status, ignoreCase: true, out var status))
            {
                return Result.Fail<KycAutoApprovalControlPage>(KycErrors.InvalidTransition);
            }

            query = query.Where(c => c.Status == status);
        }

        var total = await query.CountAsync(ct);

        // Counted over the WHOLE queue and not the filtered page: it is the screen's badge, and a
        // badge that changes when you click a filter is a badge nobody trusts.
        var pending = await db.KycAutoApprovalControls
            .CountAsync(c => c.Status == KycAutoApprovalControlStatus.Pending, ct);

        var rows = await query
            // Oldest first. A compliance queue is a backlog: the row that has been waiting longest
            // is the one whose control is most overdue, and newest-first would bury it.
            .OrderBy(c => c.SampledAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new KycAutoApprovalControlItem(
                c.Id,
                c.KycFileId,
                c.CustomerId,
                null,
                c.Status.ToString(),
                c.GrantedTier.ToString(),
                c.PolicyVersion,
                c.SampledAt,
                c.ReviewedBy,
                null,
                c.ReviewedAt,
                c.Finding,
                c.RaisedReviewId))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return Result.Ok(new KycAutoApprovalControlPage(
                rows, total, page, pageSize, pending));
        }

        var named = await ResolveNamesAsync(rows, ct);

        return Result.Ok(new KycAutoApprovalControlPage(named, total, page, pageSize, pending));
    }

    /// <summary>
    /// Fills the two display names, each in ONE batch call for the page.
    ///
    /// <para>
    /// Both are best-effort and neither can fail the page: a compliance worklist that 500s because
    /// another module is down is a worklist nobody can work. An unresolved name renders as a
    /// placeholder and the row keeps its id, which is what the officer clicks.
    /// </para>
    /// </summary>
    private async Task<List<KycAutoApprovalControlItem>> ResolveNamesAsync(
        List<KycAutoApprovalControlItem> rows, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, ClientSummary> clients;
        IReadOnlyDictionary<Guid, string> officers;

        try
        {
            clients = await customers.GetClientSummariesAsync(
                currentUser.TenantId, [.. rows.Select(r => r.CustomerId).Distinct()], ct);
        }
        catch (Exception)
        {
            clients = new Dictionary<Guid, ClientSummary>();
        }

        var officerIds = rows
            .Select(r => r.ReviewedBy)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        try
        {
            officers = officerIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await administration.GetUserNamesAsync(currentUser.TenantId, officerIds, ct);
        }
        catch (Exception)
        {
            officers = new Dictionary<Guid, string>();
        }

        return
        [
            .. rows.Select(r => r with
            {
                CustomerName = clients.TryGetValue(r.CustomerId, out var client)
                    ? client.DisplayName
                    : null,
                ReviewedByName = r.ReviewedBy is { } officer && officers.TryGetValue(officer, out var name)
                    ? name
                    : null,
            })
        ];
    }
}
