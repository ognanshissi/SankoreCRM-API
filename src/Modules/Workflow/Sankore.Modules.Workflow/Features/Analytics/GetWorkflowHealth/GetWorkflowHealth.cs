using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Modules.Workflow.Infrastructure.Notifications;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetWorkflowHealth;

/// <summary>
/// « Santé des workflows », rebuilt « à traiter d'abord » (product owner, 2026-11-03): what needs
/// someone now — open steps past their delay, and open steps waiting longer than the threshold —
/// each with its record named and who must act, then one line per circuit, versions together.
/// <para>
/// A step is « hors délai » when it had a delay and closed after it, is still open past it, or was
/// escalated or reminded for it. The old dashboard only counted <c>TimedOut</c>, which nothing sets
/// since overdue steps escalate instead of ending (2026-10-29): every circuit read 0 %. And a circuit
/// was « bloqué » from its START, not from the moment its current step opened.
/// </para>
/// </summary>
public sealed record GetWorkflowHealthQuery(int Days = 30, int StuckThresholdHours = 48)
    : IRequest<Result<WorkflowHealthDto>>;

/// <param name="SlaKeptRate">Share of the period's steps with a delay that were decided in time; null when none had one.</param>
public sealed record WorkflowHealthDto(
    DateTimeOffset AsOf,
    DateTimeOffset From,
    int Days,
    int StuckThresholdHours,
    int OverdueCount,
    int StuckCount,
    int ActiveInstances,
    int SlaSteps,
    int SlaBreached,
    double? SlaKeptRate,
    IReadOnlyList<AttentionItemDto> Attention,
    IReadOnlyList<CircuitHealthDto> Circuits);

/// <param name="Kind"><c>Overdue</c> (past its delay) or <c>Stuck</c> (no delay passed, but waiting beyond the threshold).</param>
/// <param name="RecordLabel">The record's name as the module that owns it says it; null when it is gone.</param>
/// <param name="ApproverRole">The label of the role that decides it.</param>
/// <param name="AssignedTo">The person it was handed to, by name.</param>
/// <param name="WaitingSince">When the step opened: the previous step's decision, else the circuit's start.</param>
/// <param name="Escalations">Escalations and automatic reminders since it opened.</param>
public sealed record AttentionItemDto(
    string Kind,
    Guid InstanceId,
    Guid InstanceStepId,
    Guid TemplateId,
    string Circuit,
    string StepName,
    string EntityType,
    Guid EntityId,
    string? RecordLabel,
    string? RecordLink,
    string? ApproverRoleCode,
    string? ApproverRole,
    Guid? AssignedToUserId,
    string? AssignedTo,
    DateTimeOffset WaitingSince,
    double WaitingHours,
    DateTimeOffset? DueAt,
    double? OverdueHours,
    int Escalations);

/// <param name="TemplateId">The active version, else the latest — where the circuit opens.</param>
/// <param name="Versions">How many versions the line gathers.</param>
public sealed record CircuitHealthDto(
    Guid TemplateId,
    string Name,
    string EntityType,
    int Versions,
    bool IsActive,
    int ActiveInstances,
    int OpenSteps,
    int Overdue,
    int Stuck,
    int SlaSteps,
    int SlaBreached,
    double? SlaKeptRate);

internal sealed class GetWorkflowHealthHandler(
    WorkflowDbContext db,
    ITenantContext tenant,
    TimeProvider clock,
    RecordFactsReader? facts = null,
    IAdministrationModule? administration = null)
    : IRequestHandler<GetWorkflowHealthQuery, Result<WorkflowHealthDto>>
{
    public const int MaxAttention = 100;

    public async Task<Result<WorkflowHealthDto>> Handle(GetWorkflowHealthQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var from = now.AddDays(-request.Days);
        var stuckAfter = TimeSpan.FromHours(request.StuckThresholdHours);

        var templates = await db.WorkflowTemplates
            .Select(t => new { t.Id, t.Name, t.EntityType, t.Circuit, t.Version, t.IsActive })
            .ToListAsync(ct);
        // Versions of one circuit share entity, circuit key and name: they read as one line.
        var circuitOf = templates.ToDictionary(t => t.Id, t => (t.EntityType, t.Circuit, t.Name));
        var templateName = templates.ToDictionary(t => t.Id, t => t.Name);

        var running = await db.WorkflowInstances
            .Include(i => i.Steps)
            .Where(i => i.Status == WorkflowStatus.InProgress || i.Status == WorkflowStatus.WaitingForChild)
            .ToListAsync(ct);

        // Steps with a delay, decided in the period or still open on a running circuit. The instance's
        // status is joined in SQL: a cancelled circuit leaves its open step AwaitingApproval for good,
        // and every one of those used to be loaded and dropped in memory, on every read.
        var slaSteps = await db.WorkflowInstanceSteps
            .Where(s => s.DueAt != null)
            .Join(db.WorkflowInstances, s => s.InstanceId, i => i.Id, (s, i) => new { Step = s, i.TemplateId, InstanceStatus = i.Status })
            .Where(x => (x.Step.CompletedAt != null && x.Step.CompletedAt >= from)
                     || (x.Step.Status == StepStatus.AwaitingApproval && x.InstanceStatus == WorkflowStatus.InProgress))
            .Select(x => new { x.Step.Id, x.Step.InstanceId, x.Step.StepDefinitionId, x.Step.Status, x.Step.DueAt, x.Step.CompletedAt, x.TemplateId })
            .ToListAsync(ct);
        var slaInstanceIds = slaSteps.Select(s => s.InstanceId).Distinct().ToList();

        var auditInstanceIds = slaInstanceIds.Union(running.Select(i => i.Id)).ToList();
        var escalations = (await db.WorkflowAuditEntries
                .Where(a => auditInstanceIds.Contains(a.InstanceId)
                         && (a.EventCode == EventCodes.Escalated || a.EventCode == EventCodes.SlaReminder))
                .Select(a => new { a.InstanceId, a.FromStateId, a.OccurredAt })
                .ToListAsync(ct))
            .GroupBy(a => (a.InstanceId, a.FromStateId))
            .ToDictionary(g => g.Key, g => g.Select(a => a.OccurredAt).ToList());

        bool Breached(StepStatus status, DateTimeOffset due, DateTimeOffset? completed, Guid instanceId, Guid definitionId)
            => status == StepStatus.TimedOut
            || (completed is { } c && c > due)
            || (status == StepStatus.AwaitingApproval && due < now)
            || escalations.ContainsKey((instanceId, definitionId));

        var slaRows = slaSteps
            .Select(s => new
            {
                Circuit = circuitOf.TryGetValue(s.TemplateId, out var c) ? c : default,
                Breached = Breached(s.Status, s.DueAt!.Value, s.CompletedAt, s.InstanceId, s.StepDefinitionId),
            })
            .Where(r => r.Circuit != default)
            .ToList();

        // ── What needs someone now ──
        var raw = new List<(AttentionItemDto Item, (string, string, string) Circuit)>();
        foreach (var instance in running.Where(i => i.Status == WorkflowStatus.InProgress))
        {
            // The engine's own current step, as `StepEscalator` reads it. The lowest open Order was a
            // different step once a transition routed backwards (Order 5 → 4).
            var open = instance.Steps.FirstOrDefault(s => s.Order == instance.CurrentStepOrder && s.Status == StepStatus.AwaitingApproval)
                ?? instance.Steps.Where(s => s.Status == StepStatus.AwaitingApproval).OrderBy(s => s.Order).FirstOrDefault();
            if (open is null) continue;
            // Waiting since the last step closed, whatever its Order: a step closed yesterday at Order 5
            // before routing back to Order 4 was ignored, and the wait read from an older one.
            var since = instance.Steps
                .Where(s => s.Id != open.Id && s.CompletedAt != null && s.CompletedAt <= now)
                .Select(s => s.CompletedAt)
                .Max() ?? instance.StartedAt;
            var overdue = open.DueAt is { } due && due < now;
            if (!overdue && now - since < stuckAfter) continue;

            var count = escalations.TryGetValue((instance.Id, open.StepDefinitionId), out var at) ? at.Count(t => t >= since) : 0;
            raw.Add((new AttentionItemDto(
                overdue ? "Overdue" : "Stuck",
                instance.Id, open.Id, instance.TemplateId,
                templateName.GetValueOrDefault(instance.TemplateId) ?? "",
                open.Name, instance.EntityType, instance.EntityId,
                null, null,
                open.ApproverRoleCode, null, open.AssignedToUserId, null,
                since, Math.Round((now - since).TotalHours, 1),
                open.DueAt, overdue ? Math.Round((now - open.DueAt!.Value).TotalHours, 1) : null,
                count), circuitOf.GetValueOrDefault(instance.TemplateId)));
        }

        var attention = raw
            .OrderBy(r => r.Item.Kind == "Overdue" ? 0 : 1)
            .ThenByDescending(r => r.Item.OverdueHours ?? r.Item.WaitingHours)
            .Take(MaxAttention)
            .Select(r => r.Item)
            .ToList();
        attention = await NamedAsync(attention, ct);

        // ── One line per circuit ──
        var circuits = templates
            .GroupBy(t => (t.EntityType, t.Circuit, t.Name))
            .Select(g =>
            {
                var head = g.FirstOrDefault(t => t.IsActive) ?? g.OrderByDescending(t => t.Version).First();
                var ids = g.Select(t => t.Id).ToHashSet();
                var mine = running.Where(i => ids.Contains(i.TemplateId)).ToList();
                var flagged = raw.Where(r => r.Circuit == g.Key).Select(r => r.Item).ToList();
                var sla = slaRows.Where(r => r.Circuit == g.Key).ToList();
                return new CircuitHealthDto(
                    head.Id, head.Name, head.EntityType, g.Count(), g.Any(t => t.IsActive),
                    mine.Count,
                    mine.Count(i => i.Steps.Any(s => s.Status == StepStatus.AwaitingApproval)),
                    flagged.Count(f => f.Kind == "Overdue"),
                    flagged.Count(f => f.Kind == "Stuck"),
                    sla.Count, sla.Count(r => r.Breached),
                    sla.Count == 0 ? null : Math.Round(1 - (double)sla.Count(r => r.Breached) / sla.Count, 4));
            })
            // A circuit that never ran and is not active says nothing about health.
            .Where(c => c.IsActive || c.ActiveInstances > 0 || c.SlaSteps > 0)
            .OrderByDescending(c => c.Overdue + c.Stuck)
            .ThenBy(c => c.SlaKeptRate ?? 1)
            .ThenBy(c => c.Name)
            .ToList();

        var breached = slaRows.Count(r => r.Breached);
        return Result.Ok(new WorkflowHealthDto(
            now, from, request.Days, request.StuckThresholdHours,
            raw.Count(r => r.Item.Kind == "Overdue"),
            raw.Count(r => r.Item.Kind == "Stuck"),
            running.Count,
            slaRows.Count, breached,
            slaRows.Count == 0 ? null : Math.Round(1 - (double)breached / slaRows.Count, 4),
            attention, circuits));
    }

    /// <summary>The record by its name, the role by its label, the holder by name — never an identifier on screen.</summary>
    private async Task<List<AttentionItemDto>> NamedAsync(List<AttentionItemDto> items, CancellationToken ct)
    {
        if (items.Count == 0) return items;
        var tenantId = tenant.CurrentTenantId;

        var roles = administration is null
            ? new Dictionary<string, string>()
            : await administration.GetRoleLabelsAsync(tenantId,
                items.Select(i => i.ApproverRoleCode).OfType<string>().Distinct().ToList(), ct);
        var people = administration is null
            ? new Dictionary<Guid, string>()
            : await administration.GetUserNamesAsync(tenantId,
                items.Select(i => i.AssignedToUserId).OfType<Guid>().Distinct().ToList(), ct);

        // One batched read per owning module, not one or two round trips per row.
        var records = facts is null
            ? new Dictionary<(string, Guid), RecordFacts>()
            : await facts.ReadManyAsync(tenantId, items.Select(i => (i.EntityType, i.EntityId)).ToList(), ct);

        var named = new List<AttentionItemDto>(items.Count);
        foreach (var item in items)
        {
            var record = records.GetValueOrDefault((item.EntityType, item.EntityId));
            var name = record is null ? null
                : string.Join(" ", new[] { record.FirstName, record.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
            named.Add(item with
            {
                RecordLabel = string.IsNullOrWhiteSpace(name) ? null : name,
                RecordLink = record?.Link,
                ApproverRole = item.ApproverRoleCode is { } code ? roles.GetValueOrDefault(code) ?? code : null,
                AssignedTo = item.AssignedToUserId is { } u ? people.GetValueOrDefault(u) : null,
            });
        }
        return named;
    }
}

internal static class GetWorkflowHealthEndpoint
{
    public static IEndpointRouteBuilder MapGetWorkflowHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("health", async (int? days, int? stuckThresholdHours, ISender sender, CancellationToken ct) =>
            {
                var d = Math.Clamp(days ?? 30, 1, 365);
                var stuck = Math.Clamp(stuckThresholdHours ?? 48, 1, 24 * 30);
                var result = await sender.Send(new GetWorkflowHealthQuery(d, stuck), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : Results.Problem(result.Error);
            })
            .WithName("GetWorkflowHealth")
            .WithSummary("What needs someone now (overdue and waiting steps, named) and the health of each circuit")
            .RequireAuthorization(Permissions.CanViewWorkflowAnalytics.Code)
            .Produces<WorkflowHealthDto>();
        return app;
    }
}
