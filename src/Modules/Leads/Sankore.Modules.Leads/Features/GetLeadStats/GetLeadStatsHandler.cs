namespace Sankore.Modules.Leads.Features.GetLeadStats;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLeadStatsHandler(LeadsDbContext db)
    : IRequestHandler<GetLeadStatsQuery, Result<LeadStatsDto>>
{
    public async Task<Result<LeadStatsDto>> Handle(
        GetLeadStatsQuery query, CancellationToken ct)
    {
        var baseQuery = db.Leads.AsQueryable();

        if (query.From.HasValue)
            baseQuery = baseQuery.Where(l => l.CapturedAt >= query.From.Value);
        if (query.To.HasValue)
            baseQuery = baseQuery.Where(l => l.CapturedAt <= query.To.Value);

        var total = await baseQuery.CountAsync(ct);

        if (total == 0)
        {
            return Result.Ok(new LeadStatsDto(
                Total:          0,
                ByStatus:       [],
                BySource:       [],
                ByPipelineStage: [],
                ConvertedCount: 0,
                ConversionRate: 0));
        }

        var byStatus = await baseQuery
            .GroupBy(l => l.Status)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var bySource = await baseQuery
            .GroupBy(l => l.Source)
            .Select(g => new SourceCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var byStage = await baseQuery
            .GroupBy(l => l.PipelineStage)
            .Select(g => new StageCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var convertedCount = byStatus
            .FirstOrDefault(s => s.Status == LeadStatus.Converted)?.Count ?? 0;

        var conversionRate = total > 0
            ? Math.Round((double)convertedCount / total * 100, 2)
            : 0;

        return Result.Ok(new LeadStatsDto(
            Total:           total,
            ByStatus:        byStatus,
            BySource:        bySource,
            ByPipelineStage: byStage,
            ConvertedCount:  convertedCount,
            ConversionRate:  conversionRate));
    }
}
