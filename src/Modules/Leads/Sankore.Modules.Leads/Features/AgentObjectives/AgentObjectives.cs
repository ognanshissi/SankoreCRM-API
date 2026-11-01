namespace Sankore.Modules.Leads.Features.AgentObjectives;

using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <param name="Month">« yyyy-MM ».</param>
/// <param name="SetFor">The month the targets were set for — earlier than <paramref name="Month"/> when
/// carried over, null when no objective was ever set.</param>
public sealed record AgentObjectiveDto(
    Guid AgentId,
    string? AgentName,
    string Month,
    int? FirstContactWithin24hPercent,
    int? Conversions,
    string? SetFor,
    DateTimeOffset? UpdatedAt);

public sealed record SetAgentObjectiveRequest(int? FirstContactWithin24hPercent, int? Conversions);

/// <summary>One month an objective was set for, as it was set.</summary>
public sealed record AgentObjectiveHistoryDto(
    string Month,
    int? FirstContactWithin24hPercent,
    int? Conversions,
    DateTimeOffset UpdatedAt,
    string? UpdatedByName);

/// <param name="Upcoming">The current month and the two next, as they will read (carried over or set).</param>
/// <param name="History">Every month an objective was set for, latest first (at most 24).</param>
public sealed record AgentObjectivePlanDto(
    Guid AgentId,
    string? AgentName,
    IReadOnlyList<AgentObjectiveDto> Upcoming,
    IReadOnlyList<AgentObjectiveHistoryDto> History);

public static class AgentObjectiveErrors
{
    public const string AgentNotFound = "AGENT_OBJECTIVE_AGENT_NOT_FOUND";
    public const string MonthInvalid = "AGENT_OBJECTIVE_MONTH_INVALID";
    public const string MonthClosed = "AGENT_OBJECTIVE_MONTH_CLOSED";
    public const string TargetInvalid = "AGENT_OBJECTIVE_TARGET_INVALID";

    internal static string Message(string code) => code switch
    {
        AgentNotFound => "Cet agent ne fait pas partie de votre équipe.",
        MonthInvalid => "Le mois s'écrit aaaa-mm, par exemple 2026-11.",
        MonthClosed => "Un mois passé ne se modifie plus : ses objectifs sont ceux sur lesquels l'agent a été suivi.",
        TargetInvalid => $"Le premier contact sous 24 h est un pourcentage de 0 à 100 ; les conversions vont de 0 à {AgentObjective.MaxConversions}.",
        _ => code,
    };
}

/// <summary>The one reading of an agent's objective for a month: the latest row whose month is not after it.</summary>
internal static class AgentObjectiveReader
{
    internal static readonly string[] AgentRoles = [Roles.Agent.Code, Roles.CommercialAgent.Code];

    internal static bool TryParseMonth(string? text, out DateOnly month)
    {
        month = default;
        if (!DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return false;
        month = day;
        return true;
    }

    internal static string Format(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static DateOnly CurrentMonth(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);

    internal static async Task<Dictionary<Guid, AgentObjective>> EffectiveAsync(
        LeadsDbContext db, IReadOnlyCollection<Guid> agentIds, DateOnly month, CancellationToken ct)
    {
        if (agentIds.Count == 0) return [];

        var rows = await db.AgentObjectives
            .Where(o => agentIds.Contains(o.AgentId) && o.Month <= month)
            .ToListAsync(ct);

        return rows
            .GroupBy(o => o.AgentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.Month).First());
    }

    internal static AgentObjectiveDto ToDto(Guid agentId, string? name, DateOnly month, AgentObjective? row) => new(
        agentId,
        name,
        Format(month),
        row?.FirstContactWithin24hPercent,
        row?.Conversions,
        row is null ? null : Format(row.Month),
        row?.UpdatedAt);

    /// <summary>
    /// The agents the caller manages: their team (their agency and below; the whole tenant for a
    /// super-user or a user with no agency), holding the Agent or Commercial agent role.
    /// </summary>
    internal static async Task<HashSet<Guid>> ManagedAgentsAsync(
        IAdministrationModule admin, Guid tenantId, Guid managerId, CancellationToken ct)
    {
        var team = await admin.GetTeamAgentIdsAsync(tenantId, managerId, ct);
        if (team.Count == 0) return [];
        var agents = await admin.ListActiveUsersInRolesAsync(tenantId, AgentRoles, null, ct);
        return agents.Where(team.Contains).ToHashSet();
    }
}

internal sealed record ListAgentObjectivesQuery(string? Month) : IRequest<Result<IReadOnlyList<AgentObjectiveDto>>>;

internal sealed class ListAgentObjectivesHandler(
    LeadsDbContext db, ICurrentUser me, IAdministrationModule admin, TimeProvider clock)
    : IRequestHandler<ListAgentObjectivesQuery, Result<IReadOnlyList<AgentObjectiveDto>>>
{
    public async Task<Result<IReadOnlyList<AgentObjectiveDto>>> Handle(ListAgentObjectivesQuery query, CancellationToken ct)
    {
        var month = AgentObjectiveReader.CurrentMonth(clock.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(query.Month) && !AgentObjectiveReader.TryParseMonth(query.Month, out month))
            return Result.Fail<IReadOnlyList<AgentObjectiveDto>>(AgentObjectiveErrors.MonthInvalid);

        var agents = await AgentObjectiveReader.ManagedAgentsAsync(admin, me.TenantId, me.Id, ct);
        if (agents.Count == 0) return Result.Ok<IReadOnlyList<AgentObjectiveDto>>([]);

        var names = await admin.GetUserNamesAsync(me.TenantId, agents, ct);
        var effective = await AgentObjectiveReader.EffectiveAsync(db, agents, month, ct);

        return Result.Ok<IReadOnlyList<AgentObjectiveDto>>(agents
            .Select(id => AgentObjectiveReader.ToDto(id, names.GetValueOrDefault(id), month, effective.GetValueOrDefault(id)))
            .OrderBy(o => o.AgentName ?? "￿", StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true))
            .ToList());
    }
}

internal sealed record GetAgentObjectivePlanQuery(Guid AgentId) : IRequest<Result<AgentObjectivePlanDto>>;

/// <summary>One agent's objectives for the user record (objectives in the users screen, 2026-11-01).</summary>
internal sealed class GetAgentObjectivePlanHandler(
    LeadsDbContext db, ICurrentUser me, IAdministrationModule admin, TimeProvider clock)
    : IRequestHandler<GetAgentObjectivePlanQuery, Result<AgentObjectivePlanDto>>
{
    public const int UpcomingMonths = 3;
    public const int HistoryShown = 24;

    public async Task<Result<AgentObjectivePlanDto>> Handle(GetAgentObjectivePlanQuery query, CancellationToken ct)
    {
        var agents = await AgentObjectiveReader.ManagedAgentsAsync(admin, me.TenantId, me.Id, ct);
        if (!agents.Contains(query.AgentId))
            return Result.Fail<AgentObjectivePlanDto>(AgentObjectiveErrors.AgentNotFound);

        var current = AgentObjectiveReader.CurrentMonth(clock.GetUtcNow());
        var rows = await db.AgentObjectives
            .Where(o => o.AgentId == query.AgentId)
            .OrderByDescending(o => o.Month)
            .Take(HistoryShown + UpcomingMonths)
            .ToListAsync(ct);

        var authors = rows.Select(r => r.UpdatedBy).Append(query.AgentId).Distinct().ToList();
        var names = await admin.GetUserNamesAsync(me.TenantId, authors, ct);
        var agentName = names.GetValueOrDefault(query.AgentId);

        var upcoming = Enumerable.Range(0, UpcomingMonths)
            .Select(i => current.AddMonths(i))
            .Select(month => AgentObjectiveReader.ToDto(
                query.AgentId, agentName, month, rows.Where(r => r.Month <= month).MaxBy(r => r.Month)))
            .ToList();

        var history = rows
            .Take(HistoryShown)
            .Select(r => new AgentObjectiveHistoryDto(
                AgentObjectiveReader.Format(r.Month), r.FirstContactWithin24hPercent, r.Conversions, r.UpdatedAt,
                names.GetValueOrDefault(r.UpdatedBy)))
            .ToList();

        return Result.Ok(new AgentObjectivePlanDto(query.AgentId, agentName, upcoming, history));
    }
}

internal sealed record GetMyObjectiveQuery(string? Month) : IRequest<Result<AgentObjectiveDto>>;

internal sealed class GetMyObjectiveHandler(LeadsDbContext db, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<GetMyObjectiveQuery, Result<AgentObjectiveDto>>
{
    public async Task<Result<AgentObjectiveDto>> Handle(GetMyObjectiveQuery query, CancellationToken ct)
    {
        var month = AgentObjectiveReader.CurrentMonth(clock.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(query.Month) && !AgentObjectiveReader.TryParseMonth(query.Month, out month))
            return Result.Fail<AgentObjectiveDto>(AgentObjectiveErrors.MonthInvalid);

        var effective = await AgentObjectiveReader.EffectiveAsync(db, [me.Id], month, ct);
        return Result.Ok(AgentObjectiveReader.ToDto(me.Id, null, month, effective.GetValueOrDefault(me.Id)));
    }
}

/// <summary>
/// Sets an agent's targets from <paramref name="Month"/> on. Only the current month and later:
/// a past month's objective is what the agent was measured against, and changing it afterwards
/// would rewrite their results. Setting a month that carried an earlier objective writes a new
/// row — the earlier months keep theirs.
/// </summary>
internal sealed record SetAgentObjectiveCommand(Guid AgentId, string Month, int? FirstContactWithin24hPercent, int? Conversions)
    : IRequest<Result<AgentObjectiveDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "AgentObjective";
    public string? ResourceId => $"{AgentId}:{Month}";
}

internal sealed class SetAgentObjectiveHandler(
    LeadsDbContext db, ICurrentUser me, IAdministrationModule admin, TimeProvider clock)
    : IRequestHandler<SetAgentObjectiveCommand, Result<AgentObjectiveDto>>
{
    public async Task<Result<AgentObjectiveDto>> Handle(SetAgentObjectiveCommand cmd, CancellationToken ct)
    {
        if (!AgentObjectiveReader.TryParseMonth(cmd.Month, out var month))
            return Result.Fail<AgentObjectiveDto>(AgentObjectiveErrors.MonthInvalid);

        var now = clock.GetUtcNow();
        if (month < AgentObjectiveReader.CurrentMonth(now))
            return Result.Fail<AgentObjectiveDto>(AgentObjectiveErrors.MonthClosed);

        if (cmd.FirstContactWithin24hPercent is < 0 or > 100 || cmd.Conversions is < 0 or > AgentObjective.MaxConversions)
            return Result.Fail<AgentObjectiveDto>(AgentObjectiveErrors.TargetInvalid);

        // Outside the team reads as unknown: whether a user exists elsewhere is not the caller's business.
        var agents = await AgentObjectiveReader.ManagedAgentsAsync(admin, me.TenantId, me.Id, ct);
        if (!agents.Contains(cmd.AgentId))
            return Result.Fail<AgentObjectiveDto>(AgentObjectiveErrors.AgentNotFound);

        var row = await db.AgentObjectives
            .AsTracking()
            .FirstOrDefaultAsync(o => o.AgentId == cmd.AgentId && o.Month == month, ct);

        if (row is null)
        {
            row = AgentObjective.Create(me.TenantId, cmd.AgentId, month, cmd.FirstContactWithin24hPercent, cmd.Conversions, me.Id, now);
            db.AgentObjectives.Add(row);
        }
        else
        {
            row.Set(cmd.FirstContactWithin24hPercent, cmd.Conversions, me.Id, now);
        }

        await db.SaveChangesAsync(ct);

        var names = await admin.GetUserNamesAsync(me.TenantId, [cmd.AgentId], ct);
        return Result.Ok(AgentObjectiveReader.ToDto(cmd.AgentId, names.GetValueOrDefault(cmd.AgentId), month, row));
    }
}

internal static class AgentObjectivesEndpoints
{
    internal static IEndpointRouteBuilder MapAgentObjectivesEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("agent-objectives", async (ISender sender, CancellationToken ct, string? month = null) =>
                Respond(await sender.Send(new ListAgentObjectivesQuery(month), ct)))
            .WithName("ListAgentObjectives")
            .WithTags("Leads — Analytics")
            .WithSummary("Les objectifs du mois des agents de l'équipe de l'appelant (premier contact sous 24 h, conversions)")
            .WithDescription(
                "month = aaaa-mm, le mois courant par défaut. Un mois sans objectif fixé reprend le dernier fixé avant lui "
                + "(setFor dit lequel). L'équipe = l'agence de l'appelant et ses sous-agences, rôles Agent et Agent commercial. "
                + "Requires permission: lead:agent-objective:manage.")
            .RequireAuthorization(Permissions.CanManageAgentObjectives.Code)
            .Produces<IReadOnlyList<AgentObjectiveDto>>()
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("agent-objectives/{agentId:guid}", async (Guid agentId, ISender sender, CancellationToken ct) =>
                Respond(await sender.Send(new GetAgentObjectivePlanQuery(agentId), ct)))
            .WithName("GetAgentObjectivePlan")
            .WithTags("Leads — Analytics")
            .WithSummary("Les objectifs d'un agent de l'équipe : le mois en cours et les deux suivants, et l'historique de ce qui a été fixé")
            .WithDescription(
                "404 AGENT_OBJECTIVE_AGENT_NOT_FOUND pour un agent hors de l'équipe de l'appelant. "
                + "Requires permission: lead:agent-objective:manage.")
            .RequireAuthorization(Permissions.CanManageAgentObjectives.Code)
            .Produces<AgentObjectivePlanDto>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("agent-objectives/{agentId:guid}/{month}", async (Guid agentId, string month, SetAgentObjectiveRequest req, ISender sender, CancellationToken ct) =>
                Respond(await sender.Send(new SetAgentObjectiveCommand(agentId, month, req.FirstContactWithin24hPercent, req.Conversions), ct)))
            .WithName("SetAgentObjective")
            .WithTags("Leads — Analytics")
            .WithSummary("Fixe les objectifs d'un agent de l'équipe à partir d'un mois (le mois courant ou un mois à venir)")
            .WithDescription(
                "Null = pas d'objectif à partir de ce mois. Refusé pour un mois passé (AGENT_OBJECTIVE_MONTH_CLOSED) et pour un "
                + "agent hors de l'équipe (404 AGENT_OBJECTIVE_AGENT_NOT_FOUND). Requires permission: lead:agent-objective:manage.")
            .RequireAuthorization(Permissions.CanManageAgentObjectives.Code)
            .Produces<AgentObjectiveDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("me/objective", async (ISender sender, CancellationToken ct, string? month = null) =>
                Respond(await sender.Send(new GetMyObjectiveQuery(month), ct)))
            .WithName("GetMyObjective")
            .WithTags("Leads — Analytics")
            .WithSummary("Les objectifs du mois de l'agent connecté, tels que son responsable les a fixés")
            .RequireAuthorization()
            .Produces<AgentObjectiveDto>()
            .Produces(StatusCodes.Status400BadRequest);

        return group;
    }

    private static IResult Respond<T>(Result<T> result) =>
        result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(
                title: "Objectifs",
                detail: AgentObjectiveErrors.Message(result.Error!),
                statusCode: result.Error == AgentObjectiveErrors.AgentNotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error });
}
