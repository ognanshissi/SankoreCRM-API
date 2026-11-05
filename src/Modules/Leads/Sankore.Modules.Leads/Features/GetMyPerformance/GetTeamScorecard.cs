namespace Sankore.Modules.Leads.Features.GetMyPerformance;

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
using Sankore.Shared.Kernel.Authorization;

public sealed record TeamAgencyOptionDto(Guid Id, string Name, Guid? ParentAgencyId);

public sealed record TeamAgentOptionDto(Guid Id, string FullName, Guid? AgencyId);

/// <summary>Open tasks nobody started (<see cref="CrmTaskStatus.Pending"/>), now — not over the period.</summary>
public sealed record TeamTasksDto(int NotStarted, int Overdue);

/// <param name="ToContact">Leads the agent holds now that are still <see cref="LeadStatus.New"/>.</param>
/// <param name="FirstContactRate">Within 24 h, over the period; null when no assignment is eligible yet.</param>
/// <param name="LateFirstContacts">Held leads whose first-contact deadline passed without a contact, now.</param>
public sealed record TeamAgentRowDto(
    Guid AgentId,
    string FullName,
    string? AgencyName,
    int Captured,
    int Assigned,
    int ToContact,
    int TasksNotStarted,
    int TasksOverdue,
    double? FirstContactRate,
    int LateFirstContacts,
    int Conversions);

/// <param name="Captured">Leads captured in the period: in the agencies looked at, or typed in by the agent chosen.</param>
/// <param name="LateFirstContacts">Held leads whose first-contact deadline passed without a contact, now.</param>
public sealed record TeamScorecardDto(
    string Period,
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? AgencyId,
    Guid? AgentId,
    bool AllAgencies,
    IReadOnlyList<TeamAgencyOptionDto> Agencies,
    IReadOnlyList<TeamAgentOptionDto> Agents,
    int Captured,
    ScorecardAssignedDto Assigned,
    IReadOnlyList<ScorecardWeekDto> WeeklyReceived,
    IReadOnlyList<ScorecardDayDto> DailyReceived,
    ScorecardFirstContactDto FirstContactWithin24h,
    ScorecardConversionsDto Conversions,
    TeamTasksDto Tasks,
    int LateFirstContacts,
    IReadOnlyList<TeamAgentRowDto> Rows);

public sealed record GetTeamScorecardQuery(string? Period, Guid? AgencyId, Guid? AgentId)
    : IRequest<Result<TeamScorecardDto>>;

/// <summary>
/// The team side of a manager's « Ma journée » (2026-11-03, product owner): the same figures as an
/// agent's scorecard, summed over the agents of the agencies looked at, plus what the day needs —
/// leads captured, tasks nobody started, first contacts already late — and one row per agent.
///
/// <para>
/// Perimeter (product owner): whoever covers every agency (<see cref="TeamSupervision.CoversAllAgencies"/>)
/// sees them all; anyone else their accessible agencies (<see cref="IAgencyScopeProvider"/>: their agency, its sub-agencies
/// and attributions). An agency or agent outside it is <see cref="OutOfScope"/>, never an empty page.
/// The team is everybody active in those agencies who does not supervise (product owner, 2026-11-05).
/// </para>
/// </summary>
internal sealed class GetTeamScorecardHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    IAdministrationModule admin,
    IAgencyScopeProvider agencyScope,
    TimeProvider clock)
    : IRequestHandler<GetTeamScorecardQuery, Result<TeamScorecardDto>>
{
    public const string OutOfScope = "TEAM_SCOPE_OUT_OF_PERIMETER";

    public async Task<Result<TeamScorecardDto>> Handle(GetTeamScorecardQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var period = string.IsNullOrWhiteSpace(query.Period) ? "day" : query.Period.ToLowerInvariant();
        if (Window(period, now) is not { } window)
            return Result.Fail<TeamScorecardDto>(GetMyScorecardHandler.UnknownPeriod);
        var (from, to) = window;
        var tenantId = currentUser.TenantId;

        var allAgencies = TeamSupervision.CoversAllAgencies(currentUser);
        IReadOnlySet<Guid>? accessible = allAgencies
            ? null
            : await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, currentUser.Id, ct);

        var roster = await admin.GetTeamRosterAsync(tenantId, accessible?.ToList(), ct);

        // The agency chosen and its sub-agencies, within the perimeter.
        IReadOnlyCollection<Guid>? agencies = accessible;
        if (query.AgencyId is { } agencyId)
        {
            if (roster.Agencies.All(a => a.Id != agencyId))
                return Result.Fail<TeamScorecardDto>(OutOfScope);
            agencies = Descendants(agencyId, roster.Agencies);
        }

        var team = roster.Members
            .Where(m => agencies is null || (m.AgencyId is { } a && agencies.Contains(a)))
            .ToList();
        if (query.AgentId is { } agentId)
        {
            team = team.Where(m => m.UserId == agentId).ToList();
            if (team.Count == 0)
                return Result.Fail<TeamScorecardDto>(OutOfScope);
        }
        var agentIds = team.Select(m => m.UserId).ToList();

        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weeksFrom = monday.AddDays(-7 * (GetMyScorecardHandler.WeeksShown - 1));
        var since = from < weeksFrom ? from : weeksFrom;

        // ── Assignments to the team (same rule as the agent's card) ─────────────────────
        var assignments = await db.LeadAssignments
            .Where(a => agentIds.Contains(a.AgentId) && a.CreatedAt >= since && a.CreatedAt < to)
            .Where(a => a.SupersededAt == null || a.FirstContactAt != null)
            .Join(db.Leads, a => a.LeadId, l => l.Id, (a, l) => new
            {
                a.AgentId,
                Row = new GetMyScorecardHandler.Row(a.LeadId, a.CreatedAt, a.FirstContactAt, a.SlaDeadline, a.SupersededAt, l.Status),
            })
            .ToListAsync(ct);
        var rows = assignments.Select(a => a.Row).ToList();
        var inPeriod = rows.Where(r => r.AssignedAt >= from).ToList();

        // ── Captured: by agency (the lead's, else the one it prefers), or typed in by the agent chosen ──
        var agencyList = agencies?.ToList();
        var capturedQ = db.Leads.Where(l => l.CapturedAt >= from && l.CapturedAt < to);
        // Counted per agent in SQL: every captured lead, held lead and pending task of the perimeter used
        // to be loaded, then rescanned once per agent.
        var captured = await (query.AgentId is { } capturer
                ? capturedQ.Where(l => l.OwnerId == capturer)
                : capturedQ.Where(l => agencyList == null
                    || (l.AgencyId != null && agencyList.Contains(l.AgencyId.Value))
                    || (l.AgencyId == null && l.PreferredAgencyId != null && agencyList.Contains(l.PreferredAgencyId.Value))))
            .GroupBy(l => l.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.OwnerId ?? Guid.Empty, g => g.Count, ct);
        var capturedTotal = captured.Values.Sum();

        // ── Now: held leads, late first contacts, tasks nobody started ───────────────────
        var held = await db.Leads
            .Where(l => l.CurrentAssignedId != null && agentIds.Contains(l.CurrentAssignedId.Value) && l.Status == LeadStatus.New)
            .GroupBy(l => l.CurrentAssignedId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var late = await db.LeadAssignments
            .Where(a => agentIds.Contains(a.AgentId) && a.SupersededAt == null && a.FirstContactAt == null && a.SlaDeadline < now)
            .Join(db.Leads, a => a.LeadId, l => l.Id, (a, l) => new { a.AgentId, l.Status, l.CurrentAssignmentId, AssignmentId = a.Id })
            .Where(x => x.CurrentAssignmentId == x.AssignmentId && !NotOpen.Contains(x.Status))
            .GroupBy(x => x.AgentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var notStarted = await db.CrmTasks
            .Where(t => t.AssignedAgentId != null && agentIds.Contains(t.AssignedAgentId.Value) && t.Status == CrmTaskStatus.Pending)
            .GroupBy(t => t.AssignedAgentId!.Value)
            .Select(g => new { g.Key, Count = g.Count(), Overdue = g.Count(t => t.DueAt < now) })
            .ToDictionaryAsync(g => g.Key, g => (g.Count, g.Overdue), ct);

        var conversions = await db.Leads
            .Where(l => l.CurrentAssignedId != null && agentIds.Contains(l.CurrentAssignedId.Value)
                     && l.Status == LeadStatus.Converted && l.ConvertedAt >= from && l.ConvertedAt < to)
            .GroupBy(l => l.CurrentAssignedId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        // An agent's own objectives when one is chosen; a team has none of its own.
        var objective = query.AgentId is { } one
            ? (await AgentObjectiveReader.EffectiveAsync(db, [one], AgentObjectiveReader.CurrentMonth(now), ct)).GetValueOrDefault(one)
            : null;
        var conversionsObjective = objective is null ? null
            : period == "month" ? objective.Conversions
            : period == "week" ? GetMyScorecardHandler.WeeklyShare(objective.Conversions, now)
            : null;

        var agencyNames = roster.Agencies.ToDictionary(a => a.Id, a => a.Name);
        var byAgent = assignments.ToLookup(a => a.AgentId, a => a.Row);
        var agentRows = team.Select(m =>
            {
                var mine = byAgent[m.UserId].Where(r => r.AssignedAt >= from).ToList();
                var firstContact = GetMyScorecardHandler.FirstContact(mine, now, null);
                return new TeamAgentRowDto(
                    m.UserId,
                    m.FullName,
                    m.AgencyId is { } a ? agencyNames.GetValueOrDefault(a) : null,
                    captured.GetValueOrDefault(m.UserId),
                    mine.Select(r => r.LeadId).Distinct().Count(),
                    held.GetValueOrDefault(m.UserId),
                    notStarted.GetValueOrDefault(m.UserId).Count,
                    notStarted.GetValueOrDefault(m.UserId).Overdue,
                    firstContact.Rate,
                    late.GetValueOrDefault(m.UserId),
                    conversions.GetValueOrDefault(m.UserId));
            })
            .ToList();

        return Result.Ok(new TeamScorecardDto(
            period,
            from,
            to,
            query.AgencyId,
            query.AgentId,
            accessible is null,
            roster.Agencies.Select(a => new TeamAgencyOptionDto(a.Id, a.Name, a.ParentAgencyId)).ToList(),
            roster.Members.Select(m => new TeamAgentOptionDto(m.UserId, m.FullName, m.AgencyId)).ToList(),
            capturedTotal,
            GetMyScorecardHandler.Assigned(inPeriod),
            GetMyScorecardHandler.Weeks(weeksFrom, rows),
            period == "month" ? [] : GetMyScorecardHandler.Days(monday, today, rows),
            GetMyScorecardHandler.FirstContact(inPeriod, now, objective?.FirstContactWithin24hPercent),
            new ScorecardConversionsDto(conversions.Values.Sum(), conversionsObjective),
            new TeamTasksDto(notStarted.Values.Sum(t => t.Count), notStarted.Values.Sum(t => t.Overdue)),
            late.Values.Sum(),
            agentRows));
    }

    private static readonly LeadStatus[] NotOpen =
        [LeadStatus.Converted, LeadStatus.Lost, LeadStatus.Disqualified, LeadStatus.Archived];

    /// <summary>[from, to) in UTC: today so far, else the agent card's week or month.</summary>
    internal static (DateTimeOffset From, DateTimeOffset To)? Window(string period, DateTimeOffset now) =>
        period == "day"
            ? (new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), now)
            : GetMyScorecardHandler.Window(period, now);

    /// <summary>The agency and every agency below it, among those the caller may see.</summary>
    internal static HashSet<Guid> Descendants(Guid root, IReadOnlyList<AgencySummary> agencies)
    {
        var result = new HashSet<Guid> { root };
        var frontier = new Queue<Guid>([root]);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var child in agencies.Where(a => a.ParentAgencyId == parent))
                if (result.Add(child.Id)) frontier.Enqueue(child.Id);
        }
        return result;
    }
}

internal static class GetTeamScorecardEndpoint
{
    internal static IEndpointRouteBuilder MapGetTeamScorecard(this IEndpointRouteBuilder app)
    {
        app.MapGet("me/team-scorecard", async (
                ISender sender, CancellationToken ct, string period = "day", Guid? agencyId = null, Guid? agentId = null) =>
            {
                var result = await sender.Send(new GetTeamScorecardQuery(period, agencyId, agentId), ct);
                if (result.IsSuccess) return Results.Ok(result.Value);
                return result.Error == GetTeamScorecardHandler.OutOfScope
                    ? Results.Problem(title: "Ma journée", detail: "Agence ou agent hors de votre périmètre.", statusCode: StatusCodes.Status404NotFound)
                    : Results.Problem(title: "Ma journée", detail: "Période inconnue : day, week ou month.", statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("GetTeamScorecard")
            .WithSummary("Les chiffres d'équipe de « Ma journée » d'un responsable : leads capturés, leads attribués par statut, tâches non démarrées, premiers contacts, conversions et une ligne par agent")
            .WithDescription(
                "period = day (aujourd'hui, défaut), week ou month, bornes UTC. Périmètre : toutes les agences pour Administrator, "
                + "System et SalesManager, sinon les agences accessibles de l'utilisateur ; agencyId inclut ses sous-agences. "
                + "L'équipe = comptes actifs Agent / CommercialAgent de ces agences. Tâches non démarrées, leads à contacter et "
                + "premiers contacts en retard sont lus maintenant, pas sur la période. Agence ou agent hors périmètre : 404.")
            .WithTags("Leads — Analytics")
            .RequireAuthorization(Permissions.CanViewLeadAnalytics.Code)
            .Produces<TeamScorecardDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
