using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.Dispatching.GetDispatchOverview;

/// <summary>What a dispatching rule actually does today — which is not what its settings suggest.</summary>
public enum DispatchRuleUsage
{
    /// <summary>The active rule with the highest priority: every lead without a pinned rule goes through it.</summary>
    AppliesByDefault,
    /// <summary>Not the default, but one or more lead sources pin it: it routes their leads only.</summary>
    PinnedToSources,
    /// <summary>Active, yet never reached: a higher-priority rule takes every lead and no source pins it.</summary>
    NeverReached,
    Inactive,
    /// <summary>A bucket that is not a configured rule (built-in default, manual assignments, deleted rule).</summary>
    NotARule,
}

public enum DispatchBucketKind
{
    Rule,
    /// <summary><c>DispatchingRule.Default()</c>: used when the tenant has no active rule.</summary>
    BuiltInDefault,
    /// <summary>A supervisor named the agent (<c>LeadAssignment.WasManualOverride</c>).</summary>
    Manual,
    /// <summary>Assignments recorded under a rule that no longer exists.</summary>
    DeletedRule,
}

public enum DispatchOutcome { Contacted, ToContact, Reassigned }

/// <param name="QueueCount">Leads waiting in the dispatch queue right now — not per rule: a lead no agent could take was never assigned under any.</param>
public sealed record DispatchOverviewDto(
    DateTimeOffset From,
    DateTimeOffset To,
    int QueueCount,
    IReadOnlyList<DispatchRuleOverviewDto> Rules);

/// <param name="Key">Stable id of the bucket for the screen: the rule id, or <c>default</c> / <c>manual</c>.</param>
/// <param name="Reassigned">Assignments of the period since replaced by another agent's.</param>
/// <param name="MedianDelayMinutes">From capture to the lead's first assignment, for leads first assigned in the period under this bucket.</param>
public sealed record DispatchRuleOverviewDto(
    string Key,
    Guid? RuleId,
    string Name,
    DispatchBucketKind Kind,
    DispatchRuleUsage Usage,
    DispatchingStrategy? Strategy,
    bool IsActive,
    int? Priority,
    int? MaxLeadsPerAgent,
    int? MaxTasksPerAgent,
    int? AntiMonopolyThreshold,
    TimeSpan? FirstContactSla,
    int ExcludedAgentCount,
    IReadOnlyList<string> PinnedSources,
    int Dispatched,
    int Reassigned,
    double? MedianDelayMinutes,
    IReadOnlyList<DispatchAgentDto> Agents,
    IReadOnlyList<DispatchFeedItemDto> Recent);

/// <param name="Waiting">Received in the period, not contacted yet and still theirs.</param>
/// <param name="OpenLeads">Open leads assigned to them now, whatever the rule — what the rule's ceiling is measured against.</param>
public sealed record DispatchAgentDto(Guid AgentId, string Name, int Received, int Contacted, int Waiting, int OpenLeads);

public sealed record DispatchFeedItemDto(
    Guid AssignmentId,
    Guid LeadId,
    string LeadName,
    string Source,
    Guid AgentId,
    string AgentName,
    DispatchingStrategy Strategy,
    double Score,
    DateTimeOffset At,
    DispatchOutcome Outcome);

/// <param name="From">Inclusive, UTC.</param>
/// <param name="To">Exclusive, UTC.</param>
public sealed record GetDispatchOverviewQuery(DateTimeOffset From, DateTimeOffset To)
    : IRequest<Result<DispatchOverviewDto>>;

/// <summary>
/// <c>GET leads/dispatching/overview</c> — the dispatching screen: every rule with what it routed
/// over the period, to whom, and the latest assignments.
///
/// <para>
/// The usage of each rule is computed the way <c>DispatchingRuleResolver</c> picks one — a source's
/// pinned rule, else the highest-priority active rule — because that is the thing the rules screen
/// never showed: a second active rule with a lower priority looks configured and routes nothing.
/// </para>
///
/// <para>
/// Assignments are grouped by the rule they RECORD (<c>LeadAssignment.RuleId</c>), not by today's
/// configuration: a rule edited since still owns what it routed. Whole tenant, like the reports.
/// </para>
/// </summary>
internal sealed class GetDispatchOverviewHandler(
    LeadsDbContext db,
    ITenantContext tenant,
    IAdministrationModule admin) : IRequestHandler<GetDispatchOverviewQuery, Result<DispatchOverviewDto>>
{
    public const int MaxRangeDays = 400;
    private const int FeedSize = 50;

    private static readonly LeadStatus[] Terminal =
        [LeadStatus.Converted, LeadStatus.Lost, LeadStatus.Disqualified, LeadStatus.Archived];

    public async Task<Result<DispatchOverviewDto>> Handle(GetDispatchOverviewQuery q, CancellationToken ct)
    {
        if (q.To <= q.From)
            return Result.Fail<DispatchOverviewDto>("DISPATCH_RANGE_INVALID");
        if ((q.To - q.From).TotalDays > MaxRangeDays)
            return Result.Fail<DispatchOverviewDto>("DISPATCH_RANGE_TOO_LONG");

        var rules = await db.DispatchingRules.AsNoTracking().ToListAsync(ct);

        var pins = await db.LeadSourceConfigs.AsNoTracking()
            .Where(s => s.DefaultDispatchingRuleId != null)
            .Select(s => new { RuleId = s.DefaultDispatchingRuleId!.Value, s.Label })
            .ToListAsync(ct);
        var pinsByRule = pins.GroupBy(p => p.RuleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Label).OrderBy(l => l).ToList());

        var assignments = await db.LeadAssignments.AsNoTracking()
            .Where(a => a.CreatedAt >= q.From && a.CreatedAt < q.To)
            .Select(a => new Row(a.Id, a.LeadId, a.AgentId, a.RuleId, a.WasManualOverride, a.Strategy,
                a.CompatibilityScore, a.CreatedAt, a.FirstContactAt, a.SupersededAt))
            .ToListAsync(ct);

        var leadIds = assignments.Select(a => a.LeadId).Distinct().ToList();
        var leads = leadIds.Count == 0
            ? new Dictionary<Guid, LeadInfo>()
            : await db.Leads.AsNoTracking()
                .Where(l => leadIds.Contains(l.Id))
                .Select(l => new { l.Id, l.FullName, l.Source, l.CapturedAt })
                .ToDictionaryAsync(l => l.Id, l => new LeadInfo(l.FullName, l.Source.ToString(), l.CapturedAt), ct);

        // A lead's FIRST assignment ever, to measure capture → agent without counting a later
        // reassignment as a slow first dispatch.
        var firstAssignedAt = leadIds.Count == 0
            ? new Dictionary<Guid, DateTimeOffset>()
            : await db.LeadAssignments.AsNoTracking()
                .Where(a => leadIds.Contains(a.LeadId))
                .GroupBy(a => a.LeadId)
                .Select(g => new { LeadId = g.Key, At = g.Min(a => a.CreatedAt) })
                .ToDictionaryAsync(x => x.LeadId, x => x.At, ct);

        var agentIds = assignments.Select(a => a.AgentId).Distinct().ToList();
        var names = agentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await admin.GetUserNamesAsync(tenant.CurrentTenantId, agentIds, ct);

        var openByAgent = agentIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.Leads.AsNoTracking()
                .Where(l => l.CurrentAssignedId != null && agentIds.Contains(l.CurrentAssignedId.Value)
                         && !Terminal.Contains(l.Status))
                .GroupBy(l => l.CurrentAssignedId!.Value)
                .Select(g => new { AgentId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.AgentId, x => x.Count, ct);

        var queueCount = await db.Leads.AsNoTracking()
            .CountAsync(l => l.AwaitingDispatchSince != null && l.CurrentAssignmentId == null
                          && !Terminal.Contains(l.Status), ct);

        var ruleIds = rules.Select(r => r.Id).ToHashSet();
        string KeyOf(Row a) =>
            a.WasManualOverride ? "manual"
            : a.RuleId is { } id ? (ruleIds.Contains(id) ? id.ToString() : "deleted")
            : "default";
        var byKey = assignments.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());

        // The resolver's choice for a lead with no pinned rule.
        var defaultRule = rules.Where(r => r.IsActive).OrderByDescending(r => r.Priority).FirstOrDefault();

        DispatchRuleOverviewDto Build(
            string key, Guid? ruleId, string name, DispatchBucketKind kind, DispatchRuleUsage usage, DispatchingRule? rule,
            IReadOnlyList<string> pinned)
        {
            var rows = byKey.TryGetValue(key, out var r) ? r : [];

            var delays = rows
                .Where(a => firstAssignedAt.TryGetValue(a.LeadId, out var first) && first == a.CreatedAt
                         && leads.ContainsKey(a.LeadId))
                .Select(a => Math.Max(0, (a.CreatedAt - leads[a.LeadId].CapturedAt).TotalMinutes))
                .OrderBy(m => m)
                .ToList();

            var agents = rows.GroupBy(a => a.AgentId)
                .Select(g => new DispatchAgentDto(
                    g.Key,
                    names.TryGetValue(g.Key, out var n) ? n : "Agent inconnu",
                    Received: g.Count(),
                    Contacted: g.Count(a => a.FirstContactAt != null),
                    Waiting: g.Count(a => a.FirstContactAt == null && a.SupersededAt == null),
                    OpenLeads: openByAgent.TryGetValue(g.Key, out var o) ? o : 0))
                .OrderByDescending(a => a.Received).ThenBy(a => a.Name)
                .ToList();

            var recent = rows.OrderByDescending(a => a.CreatedAt).Take(FeedSize)
                .Select(a => new DispatchFeedItemDto(
                    a.Id, a.LeadId,
                    leads.TryGetValue(a.LeadId, out var l) ? l.Name : "Lead supprimé",
                    l?.Source ?? "",
                    a.AgentId,
                    names.TryGetValue(a.AgentId, out var n) ? n : "Agent inconnu",
                    a.Strategy, a.Score, a.CreatedAt,
                    a.SupersededAt != null ? DispatchOutcome.Reassigned
                    : a.FirstContactAt != null ? DispatchOutcome.Contacted
                    : DispatchOutcome.ToContact))
                .ToList();

            return new DispatchRuleOverviewDto(
                key, ruleId, name, kind, usage,
                rule?.Strategy, rule?.IsActive ?? kind == DispatchBucketKind.BuiltInDefault, rule?.Priority,
                rule?.MaxLeadsPerAgent, rule?.MaxTasksPerAgent, rule?.AntiMonopolyThreshold, rule?.FirstContactSla,
                rule?.ExcludedAgentIds.Count ?? 0, pinned,
                Dispatched: rows.Count,
                Reassigned: rows.Count(a => a.SupersededAt != null),
                MedianDelayMinutes: Median(delays),
                agents, recent);
        }

        var result = new List<DispatchRuleOverviewDto>();

        // In the order the resolver considers them: highest priority first.
        foreach (var rule in rules.OrderByDescending(r => r.IsActive).ThenByDescending(r => r.Priority).ThenBy(r => r.Name))
        {
            var pinned = pinsByRule.TryGetValue(rule.Id, out var p) ? p : [];
            var usage = !rule.IsActive ? DispatchRuleUsage.Inactive
                : rule.Id == defaultRule?.Id ? DispatchRuleUsage.AppliesByDefault
                : pinned.Count > 0 ? DispatchRuleUsage.PinnedToSources
                : DispatchRuleUsage.NeverReached;
            result.Add(Build(rule.Id.ToString(), rule.Id, rule.Name, DispatchBucketKind.Rule, usage, rule, pinned));
        }

        // The built-in default only routes when no rule is active; shown then, or when it did route.
        if (defaultRule is null || byKey.ContainsKey("default"))
        {
            var builtIn = DispatchingRule.Default();
            result.Add(Build("default", null, "Règle par défaut (intégrée)", DispatchBucketKind.BuiltInDefault,
                defaultRule is null ? DispatchRuleUsage.AppliesByDefault : DispatchRuleUsage.NotARule, builtIn, []));
        }

        if (byKey.ContainsKey("manual"))
            result.Add(Build("manual", null, "Affectations manuelles", DispatchBucketKind.Manual,
                DispatchRuleUsage.NotARule, null, []));

        if (byKey.ContainsKey("deleted"))
            result.Add(Build("deleted", null, "Règles supprimées", DispatchBucketKind.DeletedRule,
                DispatchRuleUsage.NotARule, null, []));

        return Result.Ok(new DispatchOverviewDto(q.From, q.To, queueCount, result));
    }

    internal static double? Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return null;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private sealed record Row(
        Guid Id, Guid LeadId, Guid AgentId, Guid? RuleId, bool WasManualOverride, DispatchingStrategy Strategy,
        double Score, DateTimeOffset CreatedAt, DateTimeOffset? FirstContactAt, DateTimeOffset? SupersededAt);

    private sealed record LeadInfo(string Name, string Source, DateTimeOffset CapturedAt);
}

public static class GetDispatchOverviewEndpoint
{
    public static IEndpointRouteBuilder MapGetDispatchOverview(this IEndpointRouteBuilder app)
    {
        app.MapGet("dispatching/overview", Handle)
            .WithName("GetDispatchOverview")
            .WithTags("Lead Dispatching")
            .WithSummary("Every dispatching rule with what it routed over the period, to whom, and the latest assignments")
            .RequireAuthorization(Permissions.CanReadDispatchingRules.Code)
            .Produces<DispatchOverviewDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithOpenApi();

        return app;
    }

    /// <param name="from">Inclusive. Defaults to 7 days before <paramref name="to"/>.</param>
    /// <param name="to">Exclusive. Defaults to now.</param>
    private static async Task<IResult> Handle(
        ISender sender,
        TimeProvider clock,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-7);

        var result = await sender.Send(new GetDispatchOverviewQuery(start, end), ct);
        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return Results.Problem(
            result.Error == "DISPATCH_RANGE_TOO_LONG"
                ? $"La période ne peut pas dépasser {GetDispatchOverviewHandler.MaxRangeDays} jours."
                : "La fin de la période doit suivre son début.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error });
    }
}
