using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.Reports.GetLeadReport;

/// <param name="From">Inclusive, UTC.</param>
/// <param name="To">Exclusive, UTC.</param>
/// <param name="Granularity">Null picks one from the range so a series stays between ~7 and ~60 points.</param>
public sealed record GetLeadReportQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? AgencyId,
    ReportGranularity? Granularity) : IRequest<Result<LeadReportDto>>;

/// <summary>
/// The administrators' lead report (<c>GET leads/reports/overview</c>).
///
/// <para>
/// <b>Whole tenant, no team scoping</b> — unlike <c>agent-performance</c>, which narrows a manager
/// to their team. The permission (<c>lead:report:view</c>) is what restricts who may see this, and
/// it is granted to Administrator only by default: a report for the institution that silently
/// showed one team would be a wrong report, not a smaller one.
/// </para>
///
/// <para>
/// The time series is bucketed in memory, from the period's timestamps alone: a database-side
/// <c>date_trunc</c> would tie the query to PostgreSQL's week convention and to its session time
/// zone, and a period is a few thousand rows at most. Buckets are UTC, weeks start on Monday (ISO).
/// </para>
/// </summary>
internal sealed class GetLeadReportHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin,
    TimeProvider clock) : IRequestHandler<GetLeadReportQuery, Result<LeadReportDto>>
{
    private static readonly LeadStatus[] Terminal =
        [LeadStatus.Converted, LeadStatus.Lost, LeadStatus.Disqualified, LeadStatus.Archived];

    public const int MaxRangeDays = 400;

    public async Task<Result<LeadReportDto>> Handle(GetLeadReportQuery q, CancellationToken ct)
    {
        if (q.To <= q.From)
            return Result.Fail<LeadReportDto>("REPORT_RANGE_INVALID");
        if ((q.To - q.From).TotalDays > MaxRangeDays)
            return Result.Fail<LeadReportDto>("REPORT_RANGE_TOO_LONG");

        var granularity = q.Granularity ?? Pick(q.To - q.From);
        var now = clock.GetUtcNow();

        var leads = db.Leads.AsNoTracking();
        if (q.AgencyId is { } agencyId)
            leads = leads.Where(l => l.AgencyId == agencyId);

        // ── The cohort: captured in the period ───────────────────────────────────────────
        var cohort = leads.Where(l => l.CapturedAt >= q.From && l.CapturedAt < q.To);

        var cohortRows = await cohort
            .Select(l => new { l.CapturedAt, l.Status, l.PipelineStage, l.Source, l.AgencyId })
            .ToListAsync(ct);

        // ── Activity: conversions that happened in the period, whatever the capture date ──
        var conversions = await leads
            .Where(l => l.ConvertedAt != null && l.ConvertedAt >= q.From && l.ConvertedAt < q.To)
            .Select(l => new { ConvertedAt = l.ConvertedAt!.Value, l.CurrentAssignedId, l.AgencyId })
            .ToListAsync(ct);

        var openLeads = await leads.CountAsync(l => !Terminal.Contains(l.Status), ct);

        var openByAgent = await leads
            .Where(l => !Terminal.Contains(l.Status) && l.CurrentAssignedId != null)
            .GroupBy(l => l.CurrentAssignedId!.Value)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, ct);

        // ── Assignments of the period, same rule as agent-performance ─────────────────────
        var assignmentsQ = db.LeadAssignments.AsNoTracking()
            .Where(a => a.CreatedAt >= q.From && a.CreatedAt < q.To)
            // Taken away before any contact: not a missed contact (see GetAgentPerformanceHandler).
            .Where(a => a.SupersededAt == null || a.FirstContactAt != null);
        if (q.AgencyId is { } agency)
        {
            var agencyLeadIds = db.Leads.Where(l => l.AgencyId == agency).Select(l => l.Id);
            assignmentsQ = assignmentsQ.Where(a => agencyLeadIds.Contains(a.LeadId));
        }

        var assignments = await assignmentsQ
            .Select(a => new { a.AgentId, a.LeadId, a.CreatedAt, a.SlaDeadline, a.FirstContactAt })
            .ToListAsync(ct);

        // Where each assigned lead stands today, for the per-agent cohort conversion rate.
        var assignedLeadIds = assignments.Select(a => a.LeadId).Distinct().ToList();
        var assignedLeads = await db.Leads.AsNoTracking()
            .Where(l => assignedLeadIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Status, l.CurrentAssignedId })
            .ToDictionaryAsync(l => l.Id, ct);

        // Same predicate as GET leads/sla-breaches, counted instead of listed.
        var breachQ =
            from a in db.LeadAssignments
            join l in leads on a.LeadId equals l.Id
            where a.SupersededAt == null
               && a.FirstContactAt == null
               && a.SlaDeadline < now
               && !Terminal.Contains(l.Status)
            select a.Id;
        var openBreaches = await breachQ.CountAsync(ct);

        // ── KPIs ──────────────────────────────────────────────────────────────────────────
        var captured = cohortRows.Count;
        var cohortConverted = cohortRows.Count(r => r.Status == LeadStatus.Converted);
        var contacted = assignments.Where(a => a.FirstContactAt.HasValue).ToList();

        var kpis = new LeadReportKpis(
            Captured: captured,
            ConvertedInPeriod: conversions.Count,
            CohortConversionRate: Rate(cohortConverted, captured),
            Lost: cohortRows.Count(r => r.Status == LeadStatus.Lost),
            OpenLeads: openLeads,
            AvgFirstContactMinutes: contacted.Count > 0
                ? Math.Round(contacted.Average(a => (a.FirstContactAt!.Value - a.CreatedAt).TotalMinutes), 1)
                : null,
            SlaComplianceRate: assignments.Count > 0
                ? Rate(assignments.Count(a => a.FirstContactAt.HasValue && a.FirstContactAt.Value <= a.SlaDeadline), assignments.Count)
                : null,
            OpenSlaBreaches: openBreaches);

        // ── Series: every bucket present, zeros included, so a quiet week reads as a dip ──
        var capturedByBucket = cohortRows.GroupBy(r => Bucket(r.CapturedAt, granularity)).ToDictionary(g => g.Key, g => g.Count());
        var convertedByBucket = conversions.GroupBy(c => Bucket(c.ConvertedAt, granularity)).ToDictionary(g => g.Key, g => g.Count());
        var series = new List<LeadReportPoint>();
        for (var b = Bucket(q.From, granularity); b < q.To; b = Next(b, granularity))
            series.Add(new LeadReportPoint(b, capturedByBucket.GetValueOrDefault(b), convertedByBucket.GetValueOrDefault(b)));

        // ── Breakdowns of the cohort ──────────────────────────────────────────────────────
        var progression = Enum.GetValues<PipelineStage>()
            .Select(s => new LeadReportStage(s, cohortRows.Count(r => r.PipelineStage == s)))
            .ToList();

        var byStatus = cohortRows.GroupBy(r => r.Status)
            .Select(g => new LeadReportStatus(g.Key, g.Count()))
            .OrderByDescending(s => s.Count)
            .ToList();

        var bySource = cohortRows.GroupBy(r => r.Source)
            .Select(g =>
            {
                var conv = g.Count(r => r.Status == LeadStatus.Converted);
                return new LeadReportSource(g.Key, g.Count(), conv, Rate(conv, g.Count()));
            })
            .OrderByDescending(s => s.Captured)
            .ToList();

        // ── Agents ────────────────────────────────────────────────────────────────────────
        var convertedByAgent = conversions
            .Where(c => c.CurrentAssignedId != null)
            .GroupBy(c => c.CurrentAssignedId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var agentIds = assignments.Select(a => a.AgentId)
            .Concat(convertedByAgent.Keys)
            .Concat(openByAgent.Keys)
            .Distinct()
            .ToList();

        var names = agentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await admin.GetUserNamesAsync(tenant.CurrentTenantId, agentIds, ct);

        var agents = agentIds.Select(id =>
            {
                var mine = assignments.Where(a => a.AgentId == id).ToList();
                var within = mine.Count(a => a.FirstContactAt.HasValue && a.FirstContactAt.Value <= a.SlaDeadline);
                var late = mine.Count(a => a.FirstContactAt.HasValue && a.FirstContactAt.Value > a.SlaDeadline);
                var reached = mine.Where(a => a.FirstContactAt.HasValue).ToList();
                var myLeads = mine.Select(a => a.LeadId).Distinct()
                    .Select(leadId => assignedLeads.GetValueOrDefault(leadId))
                    .Where(l => l is not null)
                    .ToList();
                var cohortConverted = myLeads.Count(l => l!.Status == LeadStatus.Converted && l.CurrentAssignedId == id);
                return new LeadReportAgent(
                    AgentId: id,
                    Name: names.TryGetValue(id, out var n) ? n : "Utilisateur inconnu",
                    Assigned: mine.Count,
                    ContactedWithinSla: within,
                    ContactedLate: late,
                    NotContacted: mine.Count - within - late,
                    SlaComplianceRate: mine.Count > 0 ? Rate(within, mine.Count) : null,
                    AvgFirstContactMinutes: reached.Count > 0
                        ? Math.Round(reached.Average(a => (a.FirstContactAt!.Value - a.CreatedAt).TotalMinutes), 1)
                        : null,
                    Converted: convertedByAgent.GetValueOrDefault(id),
                    ConversionRate: Rate(cohortConverted, myLeads.Count),
                    OpenLeads: openByAgent.GetValueOrDefault(id));
            })
            .OrderByDescending(a => a.Converted)
            .ThenByDescending(a => a.Assigned)
            .ToList();

        // ── Agencies ──────────────────────────────────────────────────────────────────────
        var agencyGroups = cohortRows.GroupBy(r => r.AgencyId).ToList();
        var agencyNames = new Dictionary<Guid, string>();
        foreach (var id in agencyGroups.Select(g => g.Key).OfType<Guid>())
        {
            // One call per agency: a tenant has a handful, and the contract has no batch read.
            var summary = await admin.GetAgencyAsync(tenant.CurrentTenantId, id, ct);
            if (summary is not null) agencyNames[id] = summary.Name;
        }

        var agencies = agencyGroups
            .Select(g =>
            {
                var conv = g.Count(r => r.Status == LeadStatus.Converted);
                var name = g.Key is { } id ? agencyNames.GetValueOrDefault(id, "Agence inconnue") : "Sans agence";
                return new LeadReportAgency(g.Key, name, g.Count(), conv, Rate(conv, g.Count()));
            })
            .OrderByDescending(a => a.Captured)
            .ToList();

        return Result.Ok(new LeadReportDto(
            q.From, q.To, granularity, kpis, series, progression, byStatus, bySource, agents, agencies));
    }

    internal static double Rate(int part, int total) => total > 0 ? Math.Round((double)part / total * 100, 1) : 0;

    internal static ReportGranularity Pick(TimeSpan range) => range.TotalDays switch
    {
        <= 45 => ReportGranularity.Day,
        <= 200 => ReportGranularity.Week,
        _ => ReportGranularity.Month,
    };

    internal static DateTimeOffset Bucket(DateTimeOffset at, ReportGranularity g)
    {
        var d = at.UtcDateTime.Date;
        return g switch
        {
            ReportGranularity.Day => new DateTimeOffset(d, TimeSpan.Zero),
            ReportGranularity.Week => new DateTimeOffset(d.AddDays(-(((int)d.DayOfWeek + 6) % 7)), TimeSpan.Zero),
            _ => new DateTimeOffset(new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero),
        };
    }

    internal static DateTimeOffset Next(DateTimeOffset bucket, ReportGranularity g) => g switch
    {
        ReportGranularity.Day => bucket.AddDays(1),
        ReportGranularity.Week => bucket.AddDays(7),
        _ => bucket.AddMonths(1),
    };
}
