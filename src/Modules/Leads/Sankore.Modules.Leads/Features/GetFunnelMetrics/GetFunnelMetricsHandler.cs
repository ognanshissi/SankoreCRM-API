namespace Sankore.Modules.Leads.Features.GetFunnelMetrics;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetFunnelMetricsHandler(LeadsDbContext db)
    : IRequestHandler<GetFunnelMetricsQuery, Result<FunnelMetricsDto>>
{
    public async Task<Result<FunnelMetricsDto>> Handle(
        GetFunnelMetricsQuery query, CancellationToken ct)
    {
        var q = db.Leads.AsQueryable();

        if (query.From.HasValue)
            q = q.Where(l => l.CapturedAt >= query.From.Value);
        if (query.To.HasValue)
            q = q.Where(l => l.CapturedAt <= query.To.Value);
        if (query.AgencyId.HasValue)
            q = q.Where(l => l.AgencyId == query.AgencyId.Value);

        var statusCounts = await q
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var stageCounts = await q
            .GroupBy(l => l.PipelineStage)
            .Select(g => new { Stage = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Get(LeadStatus s) => statusCounts.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var total         = statusCounts.Sum(x => x.Count);
        var qualifying    = Get(LeadStatus.Qualifying);
        var qualified     = Get(LeadStatus.Qualified);
        var assigned      = statusCounts
            .Where(x => x.Status is LeadStatus.Qualified or LeadStatus.Open or LeadStatus.Nurturing)
            .Sum(x => x.Count);
        var converted     = Get(LeadStatus.Converted);
        var lost          = Get(LeadStatus.Lost);
        var disqualified  = Get(LeadStatus.Disqualified);

        double Rate(int count) => total > 0 ? Math.Round((double)count / total * 100, 2) : 0;

        var byStage = stageCounts
            .OrderBy(x => (int)x.Stage)
            .Select(x => new StageMetric(
                Stage:        x.Stage.ToString(),
                Count:        x.Count,
                SharePercent: Rate(x.Count)))
            .ToList();

        return Result.Ok(new FunnelMetricsDto(
            TotalCaptured:     total,
            Qualifying:        qualifying,
            Qualified:         qualified,
            Assigned:          assigned,
            Converted:         converted,
            Lost:              lost,
            Disqualified:      disqualified,
            QualificationRate: Rate(qualifying + qualified + assigned + converted),
            ConversionRate:    Rate(converted),
            LossRate:          Rate(lost + disqualified),
            ByPipelineStage:   byStage));
    }
}
