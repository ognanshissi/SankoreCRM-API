namespace Sankore.Modules.Leads.Features.LeadSources.ListLeadSources;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListLeadSourcesHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<ListLeadSourcesQuery, Result<PagedResult<LeadSourceListDto>>>
{
    public async Task<Result<PagedResult<LeadSourceListDto>>> Handle(
        ListLeadSourcesQuery query, CancellationToken ct)
    {
        var q = db.LeadSourceConfigs.AsQueryable();

        if (query.ChannelType.HasValue)
            q = q.Where(s => s.ChannelType == query.ChannelType.Value);
        if (query.Mode.HasValue)
            q = q.Where(s => s.Mode == query.Mode.Value);
        if (query.Status.HasValue)
            q = q.Where(s => s.Status == query.Status.Value);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLowerInvariant();
            q = q.Where(s => s.Label.ToLower().Contains(search)
                          || s.Code.ToLower().Contains(search));
        }

        var total = await q.CountAsync(ct);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);

        var now = clock.GetUtcNow();
        var sevenDaysAgo = now.AddDays(-7);

        // Load source IDs for the current page
        var sources = await q
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Code)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var sourceIds = sources.Select(s => s.Id).ToList();

        // Compute volume and last-received from ingestions in batch
        var ingestionStats = await db.LeadIngestions
            .Where(i => sourceIds.Contains(i.SourceId) && i.Status == LeadIngestionStatus.Accepted)
            .GroupBy(i => i.SourceId)
            .Select(g => new
            {
                SourceId = g.Key,
                LastReceivedAt = g.Max(i => i.IngestedAt),
                Volume7Days = g.Count(i => i.IngestedAt >= sevenDaysAgo)
            })
            .ToDictionaryAsync(x => x.SourceId, ct);

        var items = sources.Select(s =>
        {
            ingestionStats.TryGetValue(s.Id, out var stats);
            var lastReceived = stats?.LastReceivedAt;
            var volume = stats?.Volume7Days ?? 0;
            var health = ComputeHealth(s.Status, lastReceived, now);

            return new LeadSourceListDto(
                s.Id, s.Code, s.Label, s.Description,
                s.ChannelType, s.Mode, s.Status, health,
                lastReceived, volume, s.CostPerLead,
                s.IsSystem, s.DisplayOrder, s.Version, s.CreatedAt);
        }).ToList();

        return Result.Ok(new PagedResult<LeadSourceListDto>(items, total, page, size));
    }

    internal static SourceHealth ComputeHealth(
        LeadSourceStatus status, DateTimeOffset? lastReceived, DateTimeOffset now)
    {
        if (status is LeadSourceStatus.Error or LeadSourceStatus.Archived)
            return SourceHealth.Error;

        if (status == LeadSourceStatus.Active && lastReceived.HasValue
            && (now - lastReceived.Value).TotalDays > 7)
            return SourceHealth.Stale;

        return SourceHealth.Ok;
    }
}
