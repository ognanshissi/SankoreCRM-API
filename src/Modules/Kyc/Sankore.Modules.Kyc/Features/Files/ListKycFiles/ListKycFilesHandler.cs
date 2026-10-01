namespace Sankore.Modules.Kyc.Features.Files.ListKycFiles;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

internal sealed class ListKycFilesHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope)
    : IRequestHandler<ListKycFilesQuery, Result<KycFileListPage>>
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    public async Task<Result<KycFileListPage>> Handle(ListKycFilesQuery request, CancellationToken ct)
    {
        var filters = ParseFilters(request);
        if (filters.IsFailure) return Result.Fail<KycFileListPage>(filters.Error!);

        var (status, vigilance) = filters.Value;

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? DefaultPageSize : request.PageSize, 1, MaxPageSize);

        var query = await ApplyPerimeterAsync(db.KycFiles.AsQueryable(), request.AgencyId, ct);

        if (status is not null) query = query.Where(f => f.Status == status);
        if (vigilance is not null) query = query.Where(f => f.VigilanceLevel == vigilance);
        if (request.From is { } from) query = query.Where(f => f.UpdatedAt >= from);
        if (request.To is { } to) query = query.Where(f => f.UpdatedAt <= to);

        // The ranks the caller's ROLES can sign. A List, not an array: on .NET 10 an array's
        // Contains binds to the ReadOnlySpan<T> extension and stops translating to SQL.
        var myRanks = KycApproverLevels.RanksFor(currentUser.Roles).ToList();

        // The next rung is the LOWEST rank still pending — the only one DecideKycApprovalHandler
        // will accept. Expressed on level_rank and not on the level column: that one is text, and a
        // SQL MIN over it would order the ladder alphabetically.
        var projected = query.Select(f => new
        {
            File = f,
            NextPendingRank = db.KycApprovalSteps
                .Where(s => s.KycFileId == f.Id && s.Decision == KycApprovalDecision.Pending)
                .Min(s => (int?)s.LevelRank),
        });

        var totalCount = await projected.CountAsync(ct);

        // Counted over the whole perimeter, not the page: it is a badge. Empty ranks short-circuit
        // to zero rather than running a query whose answer cannot be anything else.
        var awaitingMeCount = myRanks.Count == 0
            ? 0
            : await projected.CountAsync(
                x => x.NextPendingRank != null && myRanks.Contains(x.NextPendingRank.Value), ct);

        var rows = await projected
            // Files awaiting the caller first, across the WHOLE list and not merely inside the page
            // — which is the reason level_rank exists at all.
            .OrderByDescending(x => x.NextPendingRank != null && myRanks.Contains(x.NextPendingRank.Value))
            // Oldest activity first, not newest: on a compliance worklist the file that has been
            // sitting untouched is the one that costs. KYC-F-06 asks for the awaiting ones first;
            // this second key is what makes the rest of the list a queue rather than a feed.
            .ThenBy(x => x.File.UpdatedAt)
            // Id last: UpdatedAt alone is not unique, and without a tie-break two pages can repeat
            // or skip a row.
            .ThenBy(x => x.File.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.File.Id,
                x.File.CustomerId,
                x.File.AgencyId,
                x.File.Status,
                x.File.Tier,
                x.File.VigilanceLevel,
                x.File.ConfidenceScore,
                x.File.DuplicateSuspected,
                x.File.UpdatedAt,
                x.NextPendingRank,
            })
            .ToListAsync(ct);

        return Result.Ok(new KycFileListPage(
            Rows: [.. rows.Select(r => new KycFileListItem(
                KycFileId: r.Id,
                CustomerId: r.CustomerId,
                AgencyId: r.AgencyId,
                Status: r.Status.ToString(),
                Tier: r.Tier.ToString(),
                VigilanceLevel: r.VigilanceLevel.ToString(),
                ConfidenceScore: r.ConfidenceScore,
                DuplicateSuspected: r.DuplicateSuspected,
                RequiredActionCode: RequiredAction(r.Status),
                AwaitingMe: r.NextPendingRank is { } rank && myRanks.Contains(rank),
                UpdatedAt: r.UpdatedAt))],
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize,
            AwaitingMeCount: awaitingMeCount));
    }

    /// <summary>
    /// The agency perimeter, as a predicate. Identical rule to the by-id read: a file with no agency
    /// is invisible to a restricted caller, and <c>null</c> from the provider means unrestricted.
    /// </summary>
    private async Task<IQueryable<KycFile>> ApplyPerimeterAsync(
        IQueryable<KycFile> query, Guid? requestedAgencyId, CancellationToken ct)
    {
        var perimeter = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        if (perimeter is not null)
        {
            var accessible = perimeter.ToList();
            query = query.Where(f => f.AgencyId != null && accessible.Contains(f.AgencyId.Value));
        }

        // Applied AFTER the perimeter, so it can only narrow. An agency the caller cannot see
        // intersects to nothing and the page comes back empty — which tells them nothing about
        // whether that agency exists.
        if (requestedAgencyId is { } agencyId)
            query = query.Where(f => f.AgencyId == agencyId);

        return query;
    }

    /// <summary>
    /// Filters arrive as text so an unknown value is the caller's typo rather than a 500. Rejected
    /// by name: silently ignoring an unparsable status would answer with the unfiltered list, which
    /// reads as "there are no files in that state".
    /// </summary>
    private static Result<(KycFileStatus? Status, KycVigilanceLevel? Vigilance)> ParseFilters(
        ListKycFilesQuery request)
    {
        KycFileStatus? status = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<KycFileStatus>(request.Status, ignoreCase: true, out var parsed))
                return Result.Fail<(KycFileStatus?, KycVigilanceLevel?)>(
                    $"KYC_STATUS_UNKNOWN: '{request.Status}' is not a KYC file status "
                    + $"({string.Join(", ", Enum.GetNames<KycFileStatus>())}).");

            status = parsed;
        }

        KycVigilanceLevel? vigilance = null;
        if (!string.IsNullOrWhiteSpace(request.VigilanceLevel))
        {
            if (!Enum.TryParse<KycVigilanceLevel>(request.VigilanceLevel, ignoreCase: true, out var parsed))
                return Result.Fail<(KycFileStatus?, KycVigilanceLevel?)>(
                    $"KYC_VIGILANCE_UNKNOWN: '{request.VigilanceLevel}' is not a vigilance level "
                    + $"({string.Join(", ", Enum.GetNames<KycVigilanceLevel>())}).");

            vigilance = parsed;
        }

        return Result.Ok((status, vigilance));
    }

    /// <summary>
    /// What the file is waiting for, as a code. Derived from the status in memory: it is a mapping
    /// over an enum, and pushing it into SQL would buy a CASE expression and cost the compiler's
    /// exhaustiveness check.
    /// </summary>
    private static string RequiredAction(KycFileStatus status) => status switch
    {
        KycFileStatus.Collecting => "COLLECT_DOCUMENTS",
        KycFileStatus.Verifying => "AWAIT_VERIFICATION",
        KycFileStatus.Validating => "AWAIT_APPROVAL",
        KycFileStatus.ComplementRequired => "PROVIDE_COMPLEMENT",
        KycFileStatus.UnderReview => "AWAIT_REVIEW",
        KycFileStatus.Expired => "RENEW_FILE",

        // Validated and closed files are waiting for nobody. A code rather than null so the screen
        // has one shape to render.
        _ => "NONE",
    };
}
