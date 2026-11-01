namespace Sankore.Modules.Leads.Features.GetMyPerformance;

using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.AgentObjectives;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>The leads assigned in the period, by where they stand now.</summary>
public sealed record ScorecardAssignedDto(int Total, int ToContact, int InProgress, int Converted, int Lost);

public sealed record ScorecardWeekDto(int Year, int Week, string Label, DateOnly StartsOn, int Received);

/// <summary>One day of the current week (week period only).</summary>
public sealed record ScorecardDayDto(DateOnly Date, int Received, bool IsToday);

/// <param name="Eligible">Assignments contacted, or left uncontacted past 24 h — one still inside its first
/// 24 h without a contact is neither on time nor late yet.</param>
/// <param name="Objective">The agent's monthly objective, in percent; null when none is set.</param>
public sealed record ScorecardFirstContactDto(double? Rate, int OnTime, int Eligible, int? Objective);

/// <param name="Objective">The month's conversions objective; for the week period, its share of the week
/// (<see cref="GetMyScorecardHandler.WeeklyShare"/>).</param>
public sealed record ScorecardConversionsDto(int Count, int? Objective);

/// <summary>Tasks due today (cancelled ones aside), and how many are done.</summary>
public sealed record ScorecardTasksTodayDto(int Done, int Total);

/// <param name="Source">The lead's channel (<see cref="LeadSource"/> name).</param>
/// <param name="SourceLabel">The configured source it came through, when there is one.</param>
public sealed record ScorecardAssignedLeadDto(
    Guid LeadId,
    string FullName,
    string Product,
    string Source,
    string? SourceLabel,
    DateTimeOffset AssignedAt,
    DateTimeOffset SlaDeadline,
    bool Contacted);

public sealed record ScorecardAssignedTodayDto(int Total, IReadOnlyList<ScorecardAssignedLeadDto> Items);

public sealed record MyScorecardDto(
    string Period,
    DateTimeOffset From,
    DateTimeOffset To,
    ScorecardAssignedDto Assigned,
    IReadOnlyList<ScorecardWeekDto> WeeklyReceived,
    IReadOnlyList<ScorecardDayDto> DailyReceived,
    ScorecardFirstContactDto FirstContactWithin24h,
    ScorecardConversionsDto Conversions,
    ScorecardTasksTodayDto TasksToday,
    ScorecardAssignedTodayDto AssignedToday);

public sealed record GetMyScorecardQuery(string? Period) : IRequest<Result<MyScorecardDto>>;

/// <summary>
/// The figures of the agent's « Ma journée » (redesign lot 2, 2026-10-31), over the week or the
/// calendar month so far (UTC bounds, like <see cref="GetMyPerformanceHandler"/>).
///
/// <para>
/// Same rule as the performance page for what an assignment is worth: one withdrawn before any
/// contact is not the agent's work and does not count; a lead counts once whatever its number of
/// assignments. « À contacter » is a lead still <see cref="LeadStatus.New"/>; « Perdus » is Lost,
/// Disqualified or Archived; every other open status is « En cours ».
/// </para>
/// </summary>
internal sealed class GetMyScorecardHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    TimeProvider clock)
    : IRequestHandler<GetMyScorecardQuery, Result<MyScorecardDto>>
{
    public const string UnknownPeriod = "SCORECARD_PERIOD_UNKNOWN";
    public const int AssignedTodayShown = 10;
    public const int WeeksShown = 4;
    internal static readonly TimeSpan FirstContactWindow = TimeSpan.FromHours(24);

    public async Task<Result<MyScorecardDto>> Handle(GetMyScorecardQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var period = string.IsNullOrWhiteSpace(query.Period) ? "month" : query.Period.ToLowerInvariant();
        if (Window(period, now) is not { } window)
            return Result.Fail<MyScorecardDto>(UnknownPeriod);
        var (from, to) = window;

        var me = currentUser.Id;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var weeksFrom = Monday(today).AddDays(-7 * (WeeksShown - 1));
        var since = from < weeksFrom ? from : weeksFrom;

        var rows = await db.LeadAssignments
            .Where(a => a.AgentId == me && a.CreatedAt >= since && a.CreatedAt < to)
            .Where(a => a.SupersededAt == null || a.FirstContactAt != null)
            .Join(db.Leads, a => a.LeadId, l => l.Id, (a, l) => new Row(
                a.LeadId, a.CreatedAt, a.FirstContactAt, a.SlaDeadline, a.SupersededAt, l.Status))
            .ToListAsync(ct);

        var inPeriod = rows.Where(r => r.AssignedAt >= from).ToList();

        // ── Conversions: by conversion date, to whoever held the lead then ─────────────────
        var conversions = await db.Leads
            .CountAsync(l => l.CurrentAssignedId == me && l.Status == LeadStatus.Converted
                          && l.ConvertedAt >= from && l.ConvertedAt < to, ct);

        var objective = (await AgentObjectiveReader.EffectiveAsync(db, [me], AgentObjectiveReader.CurrentMonth(now), ct))
            .GetValueOrDefault(me);

        // ── Tasks due today ─────────────────────────────────────────────────────────────
        var tomorrow = today.AddDays(1);
        var dueToday = await db.CrmTasks
            .Where(t => t.AssignedAgentId == me && t.Status != CrmTaskStatus.Cancelled && t.DueAt >= today && t.DueAt < tomorrow)
            .Select(t => t.Status)
            .ToListAsync(ct);

        return Result.Ok(new MyScorecardDto(
            period,
            from,
            to,
            Assigned(inPeriod),
            Weeks(weeksFrom, rows),
            period == "week" ? Days(Monday(today), today, rows) : [],
            FirstContact(inPeriod, now, objective?.FirstContactWithin24hPercent),
            new ScorecardConversionsDto(conversions, period == "month" ? objective?.Conversions : WeeklyShare(objective?.Conversions, now)),
            new ScorecardTasksTodayDto(dueToday.Count(s => s == CrmTaskStatus.Completed), dueToday.Count),
            await AssignedTodayAsync(me, today, ct)));
    }

    /// <summary>[from, to) in UTC: the ISO week (from Monday) or the calendar month, so far.</summary>
    internal static (DateTimeOffset From, DateTimeOffset To)? Window(string period, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return period switch
        {
            "week" => (Monday(today), now),
            "month" => (new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero), now),
            _ => null,
        };
    }

    private static DateTimeOffset Monday(DateTimeOffset day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    internal static ScorecardAssignedDto Assigned(IReadOnlyList<Row> rows)
    {
        var leads = rows.GroupBy(r => r.LeadId).Select(g => g.First().Status).ToList();
        return new ScorecardAssignedDto(
            leads.Count,
            leads.Count(s => s == LeadStatus.New),
            leads.Count(s => s is not (LeadStatus.New or LeadStatus.Converted or LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)),
            leads.Count(s => s == LeadStatus.Converted),
            leads.Count(s => s is LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived));
    }

    /// <summary>The last <see cref="WeeksShown"/> ISO weeks, the current one included and empty weeks too.</summary>
    internal static List<ScorecardWeekDto> Weeks(DateTimeOffset firstMonday, IReadOnlyList<Row> rows)
    {
        var weeks = new List<ScorecardWeekDto>();
        for (var i = 0; i < WeeksShown; i++)
        {
            var start = firstMonday.AddDays(7 * i);
            var end = start.AddDays(7);
            var dt = start.UtcDateTime;
            var week = ISOWeek.GetWeekOfYear(dt);
            weeks.Add(new ScorecardWeekDto(
                ISOWeek.GetYear(dt), week, $"S{week}", DateOnly.FromDateTime(dt),
                rows.Where(r => r.AssignedAt >= start && r.AssignedAt < end).Select(r => r.LeadId).Distinct().Count()));
        }
        return weeks;
    }

    /// <summary>
    /// The days of the current week: Monday to Friday always, Saturday and Sunday only when a lead
    /// arrived on them or when it is today — most institutions do not work weekends, and two empty
    /// bars would read as two bad days.
    /// </summary>
    internal static List<ScorecardDayDto> Days(DateTimeOffset monday, DateTimeOffset today, IReadOnlyList<Row> rows)
    {
        var days = new List<ScorecardDayDto>();
        for (var i = 0; i < 7; i++)
        {
            var start = monday.AddDays(i);
            var received = rows.Where(r => r.AssignedAt >= start && r.AssignedAt < start.AddDays(1)).Select(r => r.LeadId).Distinct().Count();
            var isToday = start == today;
            if (i >= 5 && received == 0 && !isToday) continue;
            days.Add(new ScorecardDayDto(DateOnly.FromDateTime(start.UtcDateTime), received, isToday));
        }
        return days;
    }

    /// <summary>
    /// The week's share of a monthly conversions objective: seven days of the month, rounded, and at
    /// least one when the month asks for any (product owner's mock-up, 2026-11-01: 12 a month → 3).
    /// </summary>
    internal static int? WeeklyShare(int? monthly, DateTimeOffset now)
    {
        if (monthly is not { } m) return null;
        if (m == 0) return 0;
        var days = DateTime.DaysInMonth(now.UtcDateTime.Year, now.UtcDateTime.Month);
        return Math.Max(1, (int)Math.Round(m * 7.0 / days, MidpointRounding.AwayFromZero));
    }

    internal static ScorecardFirstContactDto FirstContact(IReadOnlyList<Row> rows, DateTimeOffset now, int? objective)
    {
        var eligible = rows.Where(r => r.FirstContactAt != null || r.AssignedAt + FirstContactWindow <= now).ToList();
        var onTime = eligible.Count(r => r.FirstContactAt is { } at && at - r.AssignedAt <= FirstContactWindow);
        return new ScorecardFirstContactDto(
            eligible.Count == 0 ? null : Math.Round(100.0 * onTime / eligible.Count, 1),
            onTime,
            eligible.Count,
            objective);
    }

    /// <summary>The leads given to the agent today and still theirs, the longest waiting first.</summary>
    private async Task<ScorecardAssignedTodayDto> AssignedTodayAsync(Guid me, DateTimeOffset today, CancellationToken ct)
    {
        var rows = await db.LeadAssignments
            .Where(a => a.AgentId == me && a.CreatedAt >= today && a.SupersededAt == null)
            .Join(db.Leads, a => a.LeadId, l => l.Id, (a, l) => new { a, l })
            .Where(p => !p.l.IsAnonymized && p.l.CurrentAssignedId == me)
            .OrderBy(p => p.a.CreatedAt)
            .Select(p => new
            {
                p.l.Id, p.l.FullName, p.l.InterestedProduct, p.l.Source, p.l.LeadSourceConfigId,
                p.a.CreatedAt, p.a.SlaDeadline, p.a.FirstContactAt,
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return new ScorecardAssignedTodayDto(0, []);

        var shown = rows.Take(AssignedTodayShown).ToList();
        var label = await ProductLabels.LoadAsync(admin, currentUser.TenantId, ct);
        var sourceIds = shown.Where(r => r.LeadSourceConfigId != null).Select(r => r.LeadSourceConfigId!.Value).Distinct().ToList();
        var sources = sourceIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.LeadSourceConfigs.Where(s => sourceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Label, ct);

        return new ScorecardAssignedTodayDto(rows.Count, shown.Select(r => new ScorecardAssignedLeadDto(
            r.Id,
            r.FullName,
            label(r.InterestedProduct),
            r.Source.ToString(),
            r.LeadSourceConfigId is { } id ? sources.GetValueOrDefault(id) : null,
            r.CreatedAt,
            r.SlaDeadline,
            r.FirstContactAt != null)).ToList());
    }

    internal sealed record Row(
        Guid LeadId, DateTimeOffset AssignedAt, DateTimeOffset? FirstContactAt, DateTimeOffset SlaDeadline,
        DateTimeOffset? SupersededAt, LeadStatus Status);
}

internal static class GetMyScorecardEndpoint
{
    internal static IEndpointRouteBuilder MapGetMyScorecard(this IEndpointRouteBuilder app)
    {
        app.MapGet("me/scorecard", async (ISender sender, CancellationToken ct, string period = "month") =>
            {
                var result = await sender.Send(new GetMyScorecardQuery(period), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(title: "Ma journée", detail: "Période inconnue : week ou month.", statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("GetMyScorecard")
            .WithSummary("Les chiffres de « Ma journée » de l'agent connecté : leads attribués par statut, leads reçus par semaine, premier contact sous 24 h, conversions, tâches du jour et leads attribués aujourd'hui")
            .WithDescription(
                "period = week (semaine ISO en cours) ou month (mois civil en cours), bornes UTC. Les objectifs sont ceux que le "
                + "responsable a fixés (GET me/objective) ; en semaine, celui des conversions est la part de 7 jours de l'objectif "
                + "du mois. dailyReceived (semaine seulement) : lundi à vendredi, samedi et dimanche s'ils ont reçu des leads ou "
                + "sont aujourd'hui. Une affectation "
                + "retirée avant tout contact ne compte pas. Les leads attribués aujourd'hui : les 10 plus anciens encore détenus.")
            .WithTags("Leads — Analytics")
            .RequireAuthorization()
            .Produces<MyScorecardDto>()
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
