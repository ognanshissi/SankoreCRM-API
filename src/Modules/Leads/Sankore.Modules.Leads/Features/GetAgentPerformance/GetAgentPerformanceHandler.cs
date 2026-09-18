namespace Sankore.Modules.Leads.Features.GetAgentPerformance;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetAgentPerformanceHandler(LeadsDbContext db)
    : IRequestHandler<GetAgentPerformanceQuery, Result<IReadOnlyList<AgentPerformanceDto>>>
{
    public async Task<Result<IReadOnlyList<AgentPerformanceDto>>> Handle(
        GetAgentPerformanceQuery query, CancellationToken ct)
    {
        var assignmentsQ = db.LeadAssignments.AsQueryable();

        if (query.From.HasValue)
            assignmentsQ = assignmentsQ.Where(a => a.CreatedAt >= query.From.Value);
        if (query.To.HasValue)
            assignmentsQ = assignmentsQ.Where(a => a.CreatedAt <= query.To.Value);
        if (query.AgentId.HasValue)
            assignmentsQ = assignmentsQ.Where(a => a.AgentId == query.AgentId.Value);

        var assignments = await assignmentsQ.ToListAsync(ct);

        if (assignments.Count == 0)
            return Result.Ok<IReadOnlyList<AgentPerformanceDto>>([]);

        // Count converted leads per agent (via current assignment on the lead).
        var agentIds = assignments.Select(a => a.AgentId).Distinct().ToList();

        var convertedByAgent = await db.Leads
            .Where(l => l.CurrentAssignedId != null
                     && agentIds.Contains(l.CurrentAssignedId.Value)
                     && l.Status == LeadStatus.Converted)
            .GroupBy(l => l.CurrentAssignedId!.Value)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var convertedMap = convertedByAgent.ToDictionary(x => x.AgentId, x => x.Count);

        var grouped = assignments
            .GroupBy(a => a.AgentId)
            .Select(g =>
            {
                var all   = g.ToList();
                var total = all.Count;

                var contactedWithinSla = all.Count(a =>
                    a.FirstContactAt.HasValue && a.FirstContactAt.Value <= a.SlaDeadline);

                var contactedLate = all.Count(a =>
                    a.FirstContactAt.HasValue && a.FirstContactAt.Value > a.SlaDeadline);

                var notContacted = all.Count(a => a.FirstContactAt == null);

                var contacted = all.Where(a => a.FirstContactAt.HasValue).ToList();
                double? avgMinutes = contacted.Count > 0
                    ? contacted.Average(a =>
                        (a.FirstContactAt!.Value - a.CreatedAt).TotalMinutes)
                    : null;

                var slaRate = total > 0
                    ? Math.Round((double)contactedWithinSla / total * 100, 2)
                    : 0;

                return new AgentPerformanceDto(
                    AgentId:                g.Key,
                    TotalAssigned:          total,
                    ContactedWithinSla:     contactedWithinSla,
                    ContactedLate:          contactedLate,
                    NotContacted:           notContacted,
                    SlaComplianceRate:      slaRate,
                    AvgFirstContactMinutes: avgMinutes.HasValue
                        ? Math.Round(avgMinutes.Value, 1)
                        : null,
                    ConvertedLeads:         convertedMap.GetValueOrDefault(g.Key, 0));
            })
            .OrderByDescending(x => x.SlaComplianceRate)
            .ToList();

        return Result.Ok<IReadOnlyList<AgentPerformanceDto>>(grouped);
    }
}
