namespace Sankore.Modules.Integration.Features.Reconciliation.ListGaps;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class ListReconciliationGapsHandler(IntegrationDbContext db)
    : IRequestHandler<ListReconciliationGapsQuery, Result<ReconciliationGapPage>>
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    public async Task<Result<ReconciliationGapPage>> Handle(
        ListReconciliationGapsQuery request, CancellationToken ct)
    {
        var resolution = ParseResolution(request.Resolution);
        if (resolution.IsFailure) return Result.Fail<ReconciliationGapPage>(resolution.Error!);

        var gapType = ParseGapType(request.GapType);
        if (gapType.IsFailure) return Result.Fail<ReconciliationGapPage>(gapType.Error!);

        // Clamped rather than refused: a page index of 0 or -1 is a front-end off-by-one, and
        // Skip() with a negative count throws. Repo-wide pitfall.
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(
            request.PageSize <= 0 ? DefaultPageSize : request.PageSize, 1, MaxPageSize);

        // No IgnoreQueryFilters anywhere in this handler: it serves an HTTP request, so the
        // context's tenant filter is the isolation, and a connection id belonging to another
        // tenant narrows to an empty page rather than refusing — which tells the caller nothing
        // about whether that connection exists.
        var query = db.ReconciliationGaps.Where(g => g.Resolution == resolution.Value);

        if (request.ConnectionId is { } connectionId)
            query = query.Where(g => g.ConnectionId == connectionId);

        if (gapType.Value is { } type)
            query = query.Where(g => g.GapType == type);

        var totalCount = await query.CountAsync(ct);

        var rows = await query
            // Oldest divergence first: the figure that matters about a finding is how long it has
            // been open, and the one at the top of this list is the one an inspection will ask
            // about. Not "newest first" like the call journal, which is a diagnostic feed.
            .OrderBy(g => g.DetectedAt)
            // Id as the tie-break: one run opens many gaps inside the same millisecond, and
            // without a second key two pages can repeat or skip one of them.
            .ThenBy(g => g.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new
            {
                g.Id,
                g.RunId,
                g.ConnectionId,
                g.GapType,
                g.CrmId,
                g.ExternalId,
                g.DetailsJson,
                g.Resolution,
                g.DetectedAt,
                g.LastSeenAt,
                g.ResolvedBy,
                g.ResolvedAt,
                g.ResolutionNote,
            })
            .ToListAsync(ct);

        return Result.Ok(new ReconciliationGapPage(
            // The enums are turned into text here and not in the Select above: both columns are
            // mapped through HasConversion<string>, and asking the provider to translate
            // ToString() over them is a translation that works on one provider and throws on
            // another. The page is at most MaxPageSize rows, so the mapping is free.
            Rows: [.. rows.Select(r => new ReconciliationGapRow(
                r.Id,
                r.RunId,
                r.ConnectionId,
                r.GapType.ToString(),
                r.CrmId,
                r.ExternalId,
                r.DetailsJson,
                r.Resolution.ToString(),
                r.DetectedAt,
                r.LastSeenAt,
                r.ResolvedBy,
                r.ResolvedAt,
                r.ResolutionNote))],
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize,
            OpenCountsByType: await OpenCountsAsync(request.ConnectionId, ct),
            UndetectableGapTypes: [.. ReconciliationScope.NotDetected.Select(t => t.ToString())],
            LastRun: await LastRunAsync(request.ConnectionId, ct)));
    }

    /// <summary>
    /// Open gaps per type, for the whole scope rather than for the page.
    ///
    /// <para>
    /// Seeded with a zero for every type the comparison can look for, so a clean type is a zero
    /// rather than a missing key — a reader must be able to tell "looked for, none found" from
    /// "not in the answer". The types that cannot be looked for are deliberately NOT seeded here;
    /// they travel separately in <c>UndetectableGapTypes</c>, because a zero against them would
    /// be the exact lie this slice is built to avoid.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> OpenCountsAsync(
        Guid? connectionId, CancellationToken ct)
    {
        var counts = ReconciliationScope.Detected.ToDictionary(t => t.ToString(), _ => 0);

        var query = db.ReconciliationGaps.Where(g => g.Resolution == GapResolution.Open);

        if (connectionId is { } id)
            query = query.Where(g => g.ConnectionId == id);

        // Grouped in the database, on ix_integration_reconciliation_gap_tenant_resolution: the
        // open set is what this index exists for, and counting it row by row in memory would read
        // the whole backlog to produce four integers.
        var grouped = await query
            .GroupBy(g => g.GapType)
            .Select(g => new { GapType = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        foreach (var row in grouped) counts[row.GapType.ToString()] = row.Count;

        return counts;
    }

    private async Task<ReconciliationRunSummary?> LastRunAsync(
        Guid? connectionId, CancellationToken ct)
    {
        var query = db.ReconciliationRuns.AsQueryable();

        if (connectionId is { } id)
            query = query.Where(r => r.ConnectionId == id);

        var run = await query
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new ReconciliationRunSummary(
                r.Id,
                r.ConnectionId,
                r.StartedAt,
                r.FinishedAt,
                r.CheckedCount,
                r.GapCount,
                r.ClosedCount,
                r.FailureDetail))
            .FirstOrDefaultAsync(ct);

        return run;
    }

    /// <summary>
    /// Defaults to <see cref="GapResolution.Open"/>. An unparsable value is refused by name:
    /// ignoring it would silently answer with resolved history as if it were outstanding.
    /// </summary>
    private static Result<GapResolution> ParseResolution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Result.Ok(GapResolution.Open);

        return Enum.TryParse<GapResolution>(value.Trim(), ignoreCase: true, out var parsed)
            ? Result.Ok(parsed)
            : Result.Fail<GapResolution>(IntegrationErrors.PayloadInvalid);
    }

    private static Result<GapType?> ParseGapType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Result.Ok<GapType?>(null);

        return Enum.TryParse<GapType>(value.Trim(), ignoreCase: true, out var parsed)
            ? Result.Ok<GapType?>(parsed)
            : Result.Fail<GapType?>(IntegrationErrors.PayloadInvalid);
    }
}
