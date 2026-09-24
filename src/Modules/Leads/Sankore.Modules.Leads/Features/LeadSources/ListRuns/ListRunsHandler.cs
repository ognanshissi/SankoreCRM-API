namespace Sankore.Modules.Leads.Features.LeadSources.ListRuns;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListRunsHandler(LeadsDbContext db)
    : IRequestHandler<ListRunsQuery, Result<PagedResult<RunDto>>>
{
    public async Task<Result<PagedResult<RunDto>>> Handle(
        ListRunsQuery query, CancellationToken ct)
    {
        var q = db.LeadSourceRuns.Where(r => r.SourceId == query.SourceId);

        var total = await q.CountAsync(ct);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);

        var items = await q
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(r => new RunDto(
                r.Id, r.RunType, r.Status, r.StartedAt, r.CompletedAt,
                r.FetchedCount, r.IngestedCount, r.RejectedCount, r.DuplicateCount,
                r.ErrorMessage))
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<RunDto>(items, total, page, size));
    }
}
