namespace Sankore.Modules.Leads.Features.GetAgentPerformance;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetAgentPerformanceHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin)
    : IRequestHandler<GetAgentPerformanceQuery, Result<IReadOnlyList<AgentPerformanceDto>>>
{
    private static readonly HashSet<string> SupervisorRoles =
        ["System", "Administrator", "SalesManager", "BranchManager"];

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

        // ── Agency filter ───────────────────────────────────────────────
        if (query.AgencyId.HasValue)
        {
            var agencyLeadIds = db.Leads
                .Where(l => l.AgencyId == query.AgencyId.Value)
                .Select(l => l.Id);
            assignmentsQ = assignmentsQ.Where(a => agencyLeadIds.Contains(a.LeadId));
        }

        // ── PermissionAttribution scoping (US-M13-182) ──────────────────
        if (query.CurrentUserId.HasValue && query.CurrentUserRoles is not null)
        {
            var isSupervisor = query.CurrentUserRoles.Any(r => SupervisorRoles.Contains(r));

            if (!isSupervisor)
            {
                // Agent sees only their own performance
                assignmentsQ = assignmentsQ.Where(a => a.AgentId == query.CurrentUserId.Value);
            }
            else if (!query.CurrentUserRoles.Contains("System"))
            {
                // Manager sees their team only
                var teamIds = await admin.GetTeamAgentIdsAsync(
                    tenant.CurrentTenantId, query.CurrentUserId.Value, ct);

                var visibleIds = teamIds.Append(query.CurrentUserId.Value).ToList();
                assignmentsQ = assignmentsQ.Where(a => visibleIds.Contains(a.AgentId));
            }
        }

        var assignments = await assignmentsQ.ToListAsync(ct);

        if (assignments.Count == 0)
            return Result.Ok<IReadOnlyList<AgentPerformanceDto>>([]);

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
