namespace Sankore.Modules.Leads.Features.Ingestions.ListIngestions;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListIngestionsHandler(LeadsDbContext db)
    : IRequestHandler<ListIngestionsQuery, Result<PagedResult<IngestionDto>>>
{
    public async Task<Result<PagedResult<IngestionDto>>> Handle(
        ListIngestionsQuery query, CancellationToken ct)
    {
        var q = db.LeadIngestions
            .Where(i => i.SourceId == query.SourceId);

        if (query.Status.HasValue)
            q = q.Where(i => i.Status == query.Status.Value);

        var total = await q.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);

        var items = await q
            .OrderByDescending(i => i.IngestedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(i => new IngestionDto(
                i.Id, i.IngestedAt, i.ExternalId,
                i.Status, i.RejectionReason, i.LeadId))
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<IngestionDto>(items, total, page, size));
    }
}
