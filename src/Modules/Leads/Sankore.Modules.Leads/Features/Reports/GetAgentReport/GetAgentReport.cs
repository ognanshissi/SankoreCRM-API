using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.Reports.GetAgentReport;

/// <summary>One agent's figures over the period, with their evolution — the report's "fiche agent".</summary>
/// <param name="ConversionRate">
/// Of the distinct leads assigned to the agent in the period, the share converted today while still
/// theirs, 0–100. A cohort rate, like the report's: it cannot exceed 100 % the way "conversions in
/// the period / assignments in the period" can when old leads convert.
/// </param>
public sealed record AgentReportDto(
    Guid AgentId,
    string Name,
    int Assigned,
    int ContactedWithinSla,
    int ContactedLate,
    int NotContacted,
    double? SlaComplianceRate,
    double? AvgFirstContactMinutes,
    int Converted,
    double ConversionRate,
    int OpenLeads,
    int OpenSlaBreaches,
    IReadOnlyList<AgentReportPoint> Series,
    IReadOnlyList<LeadReportStage> Progression);

/// <param name="Assigned">Assignments received in the bucket.</param>
/// <param name="Contacted">First contacts made in the bucket, whatever the assignment date.</param>
/// <param name="Converted">Conversions in the bucket while the lead was theirs.</param>
public sealed record AgentReportPoint(DateTimeOffset PeriodStart, int Assigned, int Contacted, int Converted);

public sealed record AgentComparisonDto(
    DateTimeOffset From,
    DateTimeOffset To,
    ReportGranularity Granularity,
    IReadOnlyList<AgentReportDto> Agents);

public sealed record GetAgentReportQuery(
    IReadOnlyList<Guid> AgentIds,
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? AgencyId,
    ReportGranularity? Granularity) : IRequest<Result<AgentComparisonDto>>;

/// <summary>
/// <c>GET leads/reports/agents?agentIds=…&amp;agentIds=…</c> — one agent's detail, or two side by
/// side. Every definition is the overview's (<see cref="GetLeadReportHandler"/>): same assignment
/// rule, same SLA predicate, same conversion attribution, so an agent's row in the overview and
/// their sheet here always show the same numbers.
/// </summary>
internal sealed class GetAgentReportHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin,
    TimeProvider clock) : IRequestHandler<GetAgentReportQuery, Result<AgentComparisonDto>>
{
    /// <summary>A comparison reads well with two; four is the most the charts can tell apart.</summary>
    public const int MaxAgents = 4;

    private static readonly LeadStatus[] Terminal =
        [LeadStatus.Converted, LeadStatus.Lost, LeadStatus.Disqualified, LeadStatus.Archived];

    public async Task<Result<AgentComparisonDto>> Handle(GetAgentReportQuery q, CancellationToken ct)
    {
        var agentIds = q.AgentIds.Distinct().ToList();
        if (agentIds.Count == 0 || agentIds.Count > MaxAgents)
            return Result.Fail<AgentComparisonDto>("REPORT_AGENTS_INVALID");
        if (q.To <= q.From)
            return Result.Fail<AgentComparisonDto>("REPORT_RANGE_INVALID");
        if ((q.To - q.From).TotalDays > GetLeadReportHandler.MaxRangeDays)
            return Result.Fail<AgentComparisonDto>("REPORT_RANGE_TOO_LONG");

        var granularity = q.Granularity ?? GetLeadReportHandler.Pick(q.To - q.From);
        var now = clock.GetUtcNow();

        var leads = db.Leads.AsNoTracking();
        if (q.AgencyId is { } agencyId)
            leads = leads.Where(l => l.AgencyId == agencyId);
        var leadIds = leads.Select(l => l.Id);

        var assignments = await db.LeadAssignments.AsNoTracking()
            .Where(a => agentIds.Contains(a.AgentId) && a.CreatedAt >= q.From && a.CreatedAt < q.To)
            .Where(a => a.SupersededAt == null || a.FirstContactAt != null)
            .Where(a => leadIds.Contains(a.LeadId))
            .Select(a => new { a.AgentId, a.LeadId, a.CreatedAt, a.SlaDeadline, a.FirstContactAt })
            .ToListAsync(ct);

        // First contacts made in the period, for the "Contacted" series — an assignment from last
        // month called this week counts this week.
        var contacts = await db.LeadAssignments.AsNoTracking()
            .Where(a => agentIds.Contains(a.AgentId) && a.FirstContactAt != null
                        && a.FirstContactAt >= q.From && a.FirstContactAt < q.To)
            .Where(a => leadIds.Contains(a.LeadId))
            .Select(a => new { a.AgentId, FirstContactAt = a.FirstContactAt!.Value })
            .ToListAsync(ct);

        var conversions = await leads
            .Where(l => l.CurrentAssignedId != null && agentIds.Contains(l.CurrentAssignedId.Value)
                        && l.ConvertedAt != null && l.ConvertedAt >= q.From && l.ConvertedAt < q.To)
            .Select(l => new { AgentId = l.CurrentAssignedId!.Value, ConvertedAt = l.ConvertedAt!.Value })
            .ToListAsync(ct);

        var open = await leads
            .Where(l => l.CurrentAssignedId != null && agentIds.Contains(l.CurrentAssignedId.Value)
                        && !Terminal.Contains(l.Status))
            .GroupBy(l => l.CurrentAssignedId!.Value)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, ct);

        // Same predicate as GET leads/sla-breaches.
        var breaches = await (
                from a in db.LeadAssignments
                join l in leads on a.LeadId equals l.Id
                where agentIds.Contains(a.AgentId)
                   && a.SupersededAt == null
                   && a.FirstContactAt == null
                   && a.SlaDeadline < now
                   && !Terminal.Contains(l.Status)
                group a by a.AgentId into g
                select new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, ct);

        // The cohort of leads each agent received in the period, with where they stand today.
        var cohortLeadIds = assignments.Select(a => a.LeadId).Distinct().ToList();
        var cohort = await db.Leads.AsNoTracking()
            .Where(l => cohortLeadIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Status, l.PipelineStage, l.CurrentAssignedId })
            .ToDictionaryAsync(l => l.Id, ct);

        var names = await admin.GetUserNamesAsync(tenant.CurrentTenantId, agentIds, ct);

        var buckets = new List<DateTimeOffset>();
        for (var b = GetLeadReportHandler.Bucket(q.From, granularity); b < q.To; b = GetLeadReportHandler.Next(b, granularity))
            buckets.Add(b);

        var rows = agentIds.Select(id =>
        {
            var mine = assignments.Where(a => a.AgentId == id).ToList();
            var within = mine.Count(a => a.FirstContactAt.HasValue && a.FirstContactAt.Value <= a.SlaDeadline);
            var late = mine.Count(a => a.FirstContactAt.HasValue && a.FirstContactAt.Value > a.SlaDeadline);
            var reached = mine.Where(a => a.FirstContactAt.HasValue).ToList();

            var myLeads = mine.Select(a => a.LeadId).Distinct()
                .Select(leadId => cohort.GetValueOrDefault(leadId))
                .Where(l => l is not null)
                .ToList();
            var cohortConverted = myLeads.Count(l => l!.Status == LeadStatus.Converted && l.CurrentAssignedId == id);

            var assignedBy = mine.GroupBy(a => GetLeadReportHandler.Bucket(a.CreatedAt, granularity)).ToDictionary(g => g.Key, g => g.Count());
            var contactedBy = contacts.Where(c => c.AgentId == id)
                .GroupBy(c => GetLeadReportHandler.Bucket(c.FirstContactAt, granularity)).ToDictionary(g => g.Key, g => g.Count());
            var convertedBy = conversions.Where(c => c.AgentId == id)
                .GroupBy(c => GetLeadReportHandler.Bucket(c.ConvertedAt, granularity)).ToDictionary(g => g.Key, g => g.Count());

            return new AgentReportDto(
                AgentId: id,
                Name: names.TryGetValue(id, out var n) ? n : "Utilisateur inconnu",
                Assigned: mine.Count,
                ContactedWithinSla: within,
                ContactedLate: late,
                NotContacted: mine.Count - within - late,
                SlaComplianceRate: mine.Count > 0 ? GetLeadReportHandler.Rate(within, mine.Count) : null,
                AvgFirstContactMinutes: reached.Count > 0
                    ? Math.Round(reached.Average(a => (a.FirstContactAt!.Value - a.CreatedAt).TotalMinutes), 1)
                    : null,
                Converted: conversions.Count(c => c.AgentId == id),
                ConversionRate: GetLeadReportHandler.Rate(cohortConverted, myLeads.Count),
                OpenLeads: open.GetValueOrDefault(id),
                OpenSlaBreaches: breaches.GetValueOrDefault(id),
                Series: buckets.Select(b => new AgentReportPoint(
                    b, assignedBy.GetValueOrDefault(b), contactedBy.GetValueOrDefault(b), convertedBy.GetValueOrDefault(b))).ToList(),
                Progression: Enum.GetValues<PipelineStage>()
                    .Select(s => new LeadReportStage(s, myLeads.Count(l => l!.PipelineStage == s)))
                    .ToList());
        }).ToList();

        return Result.Ok(new AgentComparisonDto(q.From, q.To, granularity, rows));
    }
}

public static class GetAgentReportEndpoint
{
    public static IEndpointRouteBuilder MapGetAgentReport(this IEndpointRouteBuilder app)
    {
        app.MapGet("reports/agents", Handle)
            .WithName("GetAgentReport")
            .WithTags("Lead Reports")
            .WithSummary("One agent's detailed report, or up to four side by side for comparison")
            .RequireAuthorization(Permissions.CanViewLeadReports.Code)
            .Produces<AgentComparisonDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        TimeProvider clock,
        CancellationToken ct,
        [FromQuery] Guid[] agentIds,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        Guid? agencyId = null,
        ReportGranularity? granularity = null)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-30);

        var result = await sender.Send(new GetAgentReportQuery(agentIds, start, end, agencyId, granularity), ct);
        if (result.IsSuccess)
            return Results.Ok(result.Value);

        var detail = result.Error switch
        {
            "REPORT_AGENTS_INVALID" => $"Choisissez entre 1 et {GetAgentReportHandler.MaxAgents} agents.",
            "REPORT_RANGE_TOO_LONG" => $"La période ne peut pas dépasser {GetLeadReportHandler.MaxRangeDays} jours.",
            _ => "La fin de la période doit suivre son début.",
        };
        return Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error });
    }
}
