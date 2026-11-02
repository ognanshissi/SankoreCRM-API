using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetMyPerformance;
using Sankore.Modules.Leads.Features.Reports.GetLeadReport;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.Reports.GetProjectReport;

/// <param name="From">Inclusive, UTC.</param>
/// <param name="To">Exclusive, UTC.</param>
public sealed record GetProjectReportQuery(DateTimeOffset From, DateTimeOffset To, Guid? AgencyId)
    : IRequest<Result<ProjectReportDto>>;

/// <summary>
/// The « Projets » tab of the reports (reports lot 2, 2026-11-02): what was opened, won and lost in
/// the period and against the period before, the pipeline open today, products, loss reasons, the
/// time projects spend in each step, and the projects past their closing date.
///
/// <para>
/// Same perimeter as <see cref="GetLeadReportHandler"/>: the whole tenant under
/// <c>lead:report:view</c>, and a project's agency is its lead's, else its client's — a project
/// opened on an existing client has no lead.
/// </para>
/// </summary>
internal sealed class GetProjectReportHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin,
    TimeProvider clock,
    ProjectTemplates.ProjectTemplateResolver? templates = null,
    Sankore.Modules.Customers.PublicApi.ICustomersModule? customers = null)
    : IRequestHandler<GetProjectReportQuery, Result<ProjectReportDto>>
{
    /// <summary>The overdue list is a to-do list, not an export: the oldest ones first, this many.</summary>
    public const int MaxOverdue = 200;

    private const int MaxLossReasons = 8;

    private sealed record Row(
        Guid Id, string Title, string Product, OpportunityStage Stage, DateTimeOffset CreatedAt,
        DateTimeOffset? ClosedAt, string? CloseReason, DateTimeOffset? ExpectedCloseDate, decimal Amount,
        double Probability, Guid? OwnerId, Guid? StepId, Guid? TemplateVersionId, Guid? Agency);

    private static bool IsOpen(OpportunityStage s) => s is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost);

    public async Task<Result<ProjectReportDto>> Handle(GetProjectReportQuery q, CancellationToken ct)
    {
        if (q.To <= q.From)
            return Result.Fail<ProjectReportDto>("REPORT_RANGE_INVALID");
        if ((q.To - q.From).TotalDays > GetLeadReportHandler.MaxRangeDays)
            return Result.Fail<ProjectReportDto>("REPORT_RANGE_TOO_LONG");

        var now = clock.GetUtcNow();
        var previousFrom = q.From - (q.To - q.From);

        // Everything a figure can need: opened or closed since the start of the previous period,
        // or still open today.
        var raw = await db.Opportunities.AsNoTracking()
            .Where(o => o.CreatedAt >= previousFrom
                        || (o.ClosedAt != null && o.ClosedAt >= previousFrom)
                        || (o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost))
            .Select(o => new
            {
                o.Id, o.Title, o.Product, o.Stage, o.CreatedAt, o.ClosedAt, o.CloseReason, o.ExpectedCloseDate,
                Amount = o.EstimatedAmount != null ? o.EstimatedAmount.Amount : 0m,
                o.Probability, o.OwnerId, o.StepId, o.TemplateVersionId, o.CustomerEntityId,
                LeadAgency = db.Leads.Where(l => l.Id == o.LeadId).Select(l => l.AgencyId).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var customerAgencies = new Dictionary<Guid, Guid?>();
        var rows = new List<Row>();
        foreach (var r in raw)
        {
            var agency = r.LeadAgency;
            if (agency is null && r.CustomerEntityId is { } customerId && customers is not null)
            {
                if (!customerAgencies.TryGetValue(customerId, out agency))
                {
                    agency = (await customers.GetCustomerAsync(tenant.CurrentTenantId, customerId, ct))?.AgencyId;
                    customerAgencies[customerId] = agency;
                }
            }
            if (q.AgencyId is { } wanted && agency != wanted) continue;

            rows.Add(new Row(r.Id, r.Title, r.Product, r.Stage, r.CreatedAt, r.ClosedAt, r.CloseReason,
                r.ExpectedCloseDate, r.Amount, r.Probability, r.OwnerId, r.StepId, r.TemplateVersionId, agency));
        }

        var resolved = new Dictionary<(Guid?, string), ProjectTemplates.ResolvedProjectTemplate?>();
        async Task<ProjectTemplates.ResolvedProjectTemplate?> TemplateOf(Row r)
        {
            if (templates is null) return null;
            var key = (r.TemplateVersionId, r.Product);
            if (!resolved.TryGetValue(key, out var t))
                resolved[key] = t = await templates.ForProjectAsync(r.TemplateVersionId, r.Product, ct);
            return t;
        }

        var open = rows.Where(r => IsOpen(r.Stage)).ToList();
        var overdueRows = open.Where(r => r.ExpectedCloseDate is { } d && d < now).ToList();

        var kpis = new ProjectReportKpis(
            Period: PeriodKpis(rows, q.From, q.To),
            OpenProjects: open.Count,
            Overdue: overdueRows.Count);
        var previous = PeriodKpis(rows, previousFrom, q.From);

        // ── Pipeline open today, per kind of amount: a loan and a deposit are never added ──
        var pipelineRows = new List<(ProjectAmountKind Kind, decimal Amount, decimal Weighted)>();
        foreach (var r in open)
            pipelineRows.Add(((await TemplateOf(r))?.AmountKind ?? ProjectAmountKind.Generic, r.Amount, r.Amount * (decimal)r.Probability));
        var pipeline = pipelineRows.GroupBy(p => p.Kind).OrderBy(g => g.Key)
            .Select(g => new ProjectReportPipeline(g.Key, g.Count(), g.Sum(p => p.Amount), g.Sum(p => p.Weighted)))
            .ToList();

        // ── Products: the projects opened in the period, where they stand today ──────────
        var productLabel = await ProductLabels.LoadAsync(admin, tenant.CurrentTenantId, ct);
        var opened = rows.Where(r => r.CreatedAt >= q.From && r.CreatedAt < q.To).ToList();
        var byProduct = opened.GroupBy(r => productLabel(r.Product))
            .Select(g =>
            {
                var won = g.Count(r => r.Stage == OpportunityStage.ClosedWon);
                var lost = g.Count(r => r.Stage == OpportunityStage.ClosedLost);
                return new ProjectReportProduct(g.Key, g.Count(), won, lost, WinRate(won, lost), g.Sum(r => r.Amount));
            })
            .OrderByDescending(p => p.Opened)
            .ToList();

        // ── Loss reasons, as typed: grouped case- and space-insensitively ─────────────────
        var lostInPeriod = rows.Where(r => r.Stage == OpportunityStage.ClosedLost && InWindow(r.ClosedAt, q.From, q.To)).ToList();
        var lossReasons = lostInPeriod
            .GroupBy(r => string.IsNullOrWhiteSpace(r.CloseReason) ? "" : r.CloseReason.Trim().ToUpperInvariant())
            .Select(g => new ProjectReportLossReason(
                g.Key == "" ? "Sans motif" : g.First().CloseReason!.Trim(), g.Count()))
            .OrderByDescending(l => l.Count)
            .Take(MaxLossReasons)
            .ToList();

        var steps = await StepsAsync(rows, q, now, TemplateOf, ct);

        // ── Overdue projects, the most late first ──────────────────────────────────────────────
        var listed = overdueRows.OrderBy(r => r.ExpectedCloseDate).Take(MaxOverdue).ToList();
        var ownerIds = listed.Select(r => r.OwnerId).OfType<Guid>().Distinct().ToList();
        var owners = ownerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await admin.GetUserNamesAsync(tenant.CurrentTenantId, ownerIds, ct);
        var overdue = new List<ProjectReportOverdue>();
        foreach (var r in listed)
        {
            var step = (await TemplateOf(r))?.Steps.FirstOrDefault(s => s.Id == r.StepId)?.Label;
            overdue.Add(new ProjectReportOverdue(
                r.Id, r.Title, productLabel(r.Product), r.Stage, step,
                r.OwnerId, r.OwnerId is { } o ? owners.GetValueOrDefault(o, "Utilisateur inconnu") : null,
                r.ExpectedCloseDate!.Value, (int)(now - r.ExpectedCloseDate!.Value).TotalDays, r.Amount));
        }

        return Result.Ok(new ProjectReportDto(
            q.From, q.To, kpis, previous, pipeline, byProduct, lossReasons, steps, overdue));
    }

    private static bool InWindow(DateTimeOffset? at, DateTimeOffset from, DateTimeOffset to)
        => at is { } a && a >= from && a < to;

    private static double? WinRate(int won, int lost)
        => won + lost > 0 ? GetLeadReportHandler.Rate(won, won + lost) : null;

    /// <summary>One definition for the period and the period before.</summary>
    private static ProjectReportPeriodKpis PeriodKpis(IReadOnlyCollection<Row> rows, DateTimeOffset from, DateTimeOffset to)
    {
        var won = rows.Where(r => r.Stage == OpportunityStage.ClosedWon && InWindow(r.ClosedAt, from, to)).ToList();
        var lost = rows.Count(r => r.Stage == OpportunityStage.ClosedLost && InWindow(r.ClosedAt, from, to));
        return new ProjectReportPeriodKpis(
            Opened: rows.Count(r => r.CreatedAt >= from && r.CreatedAt < to),
            Won: won.Count,
            Lost: lost,
            WinRate: WinRate(won.Count, lost),
            WonAmount: won.Sum(r => r.Amount),
            AvgCycleDays: won.Count > 0
                ? Math.Round(won.Average(r => (r.ClosedAt!.Value - r.CreatedAt).TotalDays), 1)
                : null);
    }

    /// <summary>
    /// The time spent in each step, read off the projects' history: a step is left at a
    /// « StepChanged » line, and was entered at the previous one — or at the project's creation for
    /// its first step. Counted for the steps LEFT in the period; the projects still in a step today
    /// give how many wait there and for how long. Grouped by template and step label: « Analyse »
    /// of a loan and « Analyse » of a savings account are not the same step.
    /// </summary>
    private async Task<List<ProjectReportStep>> StepsAsync(
        List<Row> rows, GetProjectReportQuery q, DateTimeOffset now,
        Func<Row, Task<ProjectTemplates.ResolvedProjectTemplate?>> templateOf, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var changes = (await db.ProjectHistory.AsNoTracking()
                .Where(h => h.Kind == ProjectHistoryKind.StepChanged && ids.Contains(h.OpportunityId))
                .Select(h => new { h.OpportunityId, h.At, h.From, h.To })
                .ToListAsync(ct))
            .GroupBy(h => h.OpportunityId)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.At).ToList());

        var left = new List<(string Template, string Step, double Days)>();
        var waiting = new List<(string Template, string Step, double Days)>();
        foreach (var r in rows)
        {
            var template = await templateOf(r);
            var templateName = template?.Name ?? "Parcours";
            var entered = r.CreatedAt;
            string? current = null;
            foreach (var change in changes.GetValueOrDefault(r.Id) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(change.From) && change.At >= q.From && change.At < q.To)
                    left.Add((templateName, change.From!, (change.At - entered).TotalDays));
                entered = change.At;
                current = change.To;
            }

            if (IsOpen(r.Stage))
            {
                current = template?.Steps.FirstOrDefault(s => s.Id == r.StepId)?.Label ?? current;
                if (!string.IsNullOrWhiteSpace(current))
                    waiting.Add((templateName, current!, (now - entered).TotalDays));
            }
        }

        return left.Select(l => (l.Template, l.Step)).Concat(waiting.Select(w => (w.Template, w.Step))).Distinct()
            .Select(key =>
            {
                var done = left.Where(l => l.Template == key.Template && l.Step == key.Step).ToList();
                var pending = waiting.Where(w => w.Template == key.Template && w.Step == key.Step).ToList();
                return new ProjectReportStep(
                    key.Template, key.Step,
                    Left: done.Count,
                    AvgDays: done.Count > 0 ? Math.Round(done.Average(d => d.Days), 1) : null,
                    WaitingNow: pending.Count,
                    AvgWaitingDays: pending.Count > 0 ? Math.Round(pending.Average(w => w.Days), 1) : null);
            })
            .OrderByDescending(s => s.AvgDays ?? 0)
            .ThenByDescending(s => s.WaitingNow)
            .ToList();
    }
}
