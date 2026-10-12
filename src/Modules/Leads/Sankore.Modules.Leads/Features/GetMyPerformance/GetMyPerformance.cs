namespace Sankore.Modules.Leads.Features.GetMyPerformance;

using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

public sealed record GetMyPerformanceQuery(string Period) : IRequest<Result<MyPerformanceDto>>;

/// <summary>
/// La performance de l'appelant, et celle de son agence sur la même période.
///
/// <para>
/// Mêmes règles que <c>GET agent-performance</c> pour ce qu'elles ont en commun : une affectation
/// retirée avant tout contact n'est pas du travail de l'agent et ne compte pas ; une affectation
/// contactée compte, même remplacée depuis. Un lead converti l'est au crédit de l'agent qui le
/// détenait à la conversion (<c>CurrentAssignedId</c>), faute d'auteur de conversion enregistré.
/// </para>
///
/// <para>
/// Les bornes sont en UTC. L'agence est celle des leads (<c>Lead.AgencyId</c>), pas celle des
/// agents : c'est la même dans l'immense majorité des cas, et c'est la seule qui ne demande pas
/// d'énumérer les utilisateurs d'une agence à travers un autre module.
/// </para>
/// </summary>
internal sealed class GetMyPerformanceHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    IWorkflowModule workflow,
    ILeadSettings settings,
    TimeProvider clock)
    : IRequestHandler<GetMyPerformanceQuery, Result<MyPerformanceDto>>
{
    public const string FirstContactStep = "Premier contact";

    public async Task<Result<MyPerformanceDto>> Handle(GetMyPerformanceQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (Window(query.Period, now) is not { } window)
            return Result.Fail<MyPerformanceDto>("Période inconnue : month, 30d ou quarter.");
        var (from, to) = window;

        var me = currentUser.Id;
        var tenantId = currentUser.TenantId;
        var agent = await admin.GetAgentAsync(me, ct);
        Guid? agencyId = agent is { AgencyId: var a } && a != Guid.Empty ? a : null;
        var agency = agencyId is { } id ? await admin.GetAgencyAsync(tenantId, id, ct) : null;

        // ── Affectations de la période : les miennes, et celles de mon agence ──────────────
        var rows = await db.LeadAssignments
            .Where(x => x.CreatedAt >= from && x.CreatedAt < to)
            .Where(x => x.SupersededAt == null || x.FirstContactAt != null)
            .Join(db.Leads, x => x.LeadId, l => l.Id, (x, l) => new { x, l })
            .Where(p => p.x.AgentId == me || (agencyId != null && p.l.AgencyId == agencyId))
            .Select(p => new AssignmentRow(
                p.x.AgentId, p.x.LeadId, p.x.CreatedAt, p.x.FirstContactAt, p.x.SlaDeadline,
                p.l.AgencyId, p.l.Status, p.l.ConvertedAt, p.l.CurrentAssignedId, p.l.ProductLineId, p.l.InterestedProduct))
            .ToListAsync(ct);

        var mine = rows.Where(r => r.AgentId == me).ToList();
        var agencyRows = agencyId is null ? [] : rows.Where(r => r.LeadAgencyId == agencyId).ToList();

        // ── Échéances : tâches échues sur la période ────────────────────────────────────
        var dueUntil = to < now ? to : now;
        var agencyLeadIds = agencyRows.Select(r => r.LeadId).Distinct().ToList();
        var tasks = await db.CrmTasks
            .Where(t => t.Status != CrmTaskStatus.Cancelled && t.DueAt >= from && t.DueAt < dueUntil)
            .Where(t => t.AssignedAgentId == me || (t.LeadId != null && agencyLeadIds.Contains(t.LeadId.Value)))
            .Select(t => new TaskRow(t.AssignedAgentId, t.LeadId, t.DueAt, t.CompletedAt))
            .ToListAsync(ct);

        var agentFigures = Figures(mine, tasks.Where(t => t.AgentId == me).ToList(), perAgent: false);
        var agencyFigures = agencyId is null
            ? null
            : Figures(agencyRows, tasks.Where(t => t.LeadId != null && agencyLeadIds.Contains(t.LeadId.Value)).ToList(), perAgent: true);

        // ── Conversions par semaine : date de conversion, pas date d'affectation ───────
        var conversions = await db.Leads
            .Where(l => l.CurrentAssignedId == me && l.Status == LeadStatus.Converted
                     && l.ConvertedAt >= from && l.ConvertedAt < to)
            .Select(l => l.ConvertedAt!.Value)
            .ToListAsync(ct);

        // ── SLA par étape ─────────────────────────────────────────────────────────────
        var contacted = mine.Where(r => r.FirstContactAt != null || r.SlaDeadline < now).ToList();
        var stepSla = new List<StepSlaDto>
        {
            Step(FirstContactStep, contacted.Count, contacted.Count(r => r.FirstContactAt <= r.SlaDeadline)),
        };
        var workflowSteps = await workflow.GetStepSlaStatsAsync(tenantId, me, from, to, ct);
        stepSla.AddRange(workflowSteps
            .Where(s => !string.Equals(s.StepName, FirstContactStep, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.StepName, StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true))
            .Select(s => Step(s.StepName, s.Total, s.OnTime)));

        var target = await settings.GetIntAsync(tenantId, LeadSettingKeys.SlaComplianceTargetPercent, ct);

        return Result.Ok(new MyPerformanceDto(
            query.Period,
            from,
            to,
            agency?.Name,
            agentFigures,
            agencyFigures,
            Weeks(from, to, conversions),
            conversions.Count,
            stepSla,
            Math.Clamp(target, 0, 100),
            await ProductsAsync(tenantId, mine, ct)));
    }

    /// <summary>[from, to) en UTC. « Ce mois » et « Trimestre » partent du début de la période civile.</summary>
    internal static (DateTimeOffset From, DateTimeOffset To)? Window(string? period, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return period?.ToLowerInvariant() switch
        {
            "month" => (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), now),
            "30d" or null or "" => (today.AddDays(-29), now),
            "quarter" => (new DateTimeOffset(now.Year, (now.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero), now),
            _ => null,
        };
    }

    internal static PerformanceFiguresDto Figures(IReadOnlyList<AssignmentRow> rows, IReadOnlyList<TaskRow> tasks, bool perAgent)
    {
        var handled = rows.Select(r => (r.AgentId, r.LeadId)).Distinct().ToList();
        var agents = Math.Max(1, rows.Select(r => r.AgentId).Distinct().Count());
        var converted = rows
            .Where(r => r.Status == LeadStatus.Converted && r.CurrentAssignedId == r.AgentId)
            .Select(r => (r.AgentId, r.LeadId)).Distinct().Count();

        var delays = rows.Where(r => r.FirstContactAt != null)
            .Select(r => (r.FirstContactAt!.Value - r.AssignedAt).TotalMinutes).ToList();

        var onTime = tasks.Count(t => t.CompletedAt != null && t.CompletedAt <= t.DueAt);

        return new PerformanceFiguresDto(
            LeadsHandled: perAgent ? Math.Round((double)handled.Count / agents, 1) : handled.Count,
            ConversionRate: handled.Count == 0 ? null : Math.Round(100.0 * converted / handled.Count, 1),
            AvgFirstContactMinutes: delays.Count == 0 ? null : Math.Round(delays.Average(), 1),
            DeadlinesRespectedRate: tasks.Count == 0 ? null : Math.Round(100.0 * onTime / tasks.Count, 1));
    }

    /// <summary>Toutes les semaines ISO de la période, vides comprises : un trou est une information.</summary>
    internal static List<WeeklyConversionsDto> Weeks(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<DateTimeOffset> conversions)
    {
        var counts = conversions
            .GroupBy(c => (ISOWeek.GetYear(c.UtcDateTime), ISOWeek.GetWeekOfYear(c.UtcDateTime)))
            .ToDictionary(g => g.Key, g => g.Count());

        var weeks = new List<WeeklyConversionsDto>();
        var day = DateOnly.FromDateTime(from.UtcDateTime);
        var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        var last = DateOnly.FromDateTime(to.UtcDateTime.AddTicks(-1));
        for (var d = monday; d <= last; d = d.AddDays(7))
        {
            var dt = d.ToDateTime(TimeOnly.MinValue);
            var key = (ISOWeek.GetYear(dt), ISOWeek.GetWeekOfYear(dt));
            weeks.Add(new WeeklyConversionsDto(key.Item1, key.Item2, $"S{key.Item2}", d, counts.GetValueOrDefault(key)));
        }
        return weeks;
    }

    private async Task<List<ProductPerformanceDto>> ProductsAsync(Guid tenantId, IReadOnlyList<AssignmentRow> mine, CancellationToken ct)
    {
        if (mine.Count == 0) return [];

        // Le catalogue nomme une ligne résolue ; sinon le libellé capturé, et « Unknown » — la
        // valeur par défaut des captures sans produit — n'est pas un produit.
        var catalog = mine.Any(r => r.ProductLineId != null)
            ? (await admin.ListProductsAsync(tenantId, null, ct)).Where(p => p.Id != Guid.Empty).ToDictionary(p => p.Id, p => p.Name)
            : [];

        string Label(AssignmentRow r) =>
            r.ProductLineId is { } id && catalog.TryGetValue(id, out var name) ? name
            : string.IsNullOrWhiteSpace(r.InterestedProduct) || r.InterestedProduct == "Unknown" ? "Produit non précisé"
            : r.InterestedProduct.Trim();

        return mine
            .GroupBy(r => r.LeadId)
            .Select(g => g.OrderBy(r => r.AssignedAt).First())
            .GroupBy(Label)
            .Select(g =>
            {
                var converted = g.Where(r => r.Status == LeadStatus.Converted && r.CurrentAssignedId == r.AgentId && r.ConvertedAt != null).ToList();
                return new ProductPerformanceDto(
                    g.Key,
                    g.Count(),
                    converted.Count,
                    Math.Round(100.0 * converted.Count / g.Count(), 1),
                    converted.Count == 0 ? null : Math.Round(converted.Average(r => (r.ConvertedAt!.Value - r.AssignedAt).TotalDays), 1));
            })
            .OrderByDescending(p => p.Received)
            .ThenBy(p => p.Product)
            .ToList();
    }

    private static StepSlaDto Step(string name, int total, int onTime)
        => new(name, total, onTime, total == 0 ? null : Math.Round(100.0 * onTime / total, 1));
}

internal sealed record AssignmentRow(
    Guid AgentId, Guid LeadId, DateTimeOffset AssignedAt, DateTimeOffset? FirstContactAt, DateTimeOffset SlaDeadline,
    Guid? LeadAgencyId, LeadStatus Status, DateTimeOffset? ConvertedAt, Guid? CurrentAssignedId,
    Guid? ProductLineId, string? InterestedProduct);

internal sealed record TaskRow(Guid? AgentId, Guid? LeadId, DateTimeOffset DueAt, DateTimeOffset? CompletedAt);

internal static class GetMyPerformanceEndpoint
{
    internal static IEndpointRouteBuilder MapGetMyPerformance(this IEndpointRouteBuilder app)
    {
        app.MapGet("me/performance", async (ISender sender, CancellationToken ct, string period = "30d") =>
            {
                var result = await sender.Send(new GetMyPerformanceQuery(period), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("GetMyPerformance")
            .WithSummary("Performance de l'agent connecté (leads, conversion, premier contact, échéances, SLA par étape, produits), comparée à son agence.")
            .WithTags("Leads — Analytics")
            .RequireAuthorization()
            .Produces<MyPerformanceDto>()
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
