namespace Sankore.Modules.Leads.Features.GetLeadTimeline;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetLeadTimelineHandler(LeadsDbContext db)
    : IRequestHandler<GetLeadTimelineQuery, Result<IReadOnlyList<TimelineEvent>>>
{
    public async Task<Result<IReadOnlyList<TimelineEvent>>> Handle(
        GetLeadTimelineQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<TimelineEvent>>("LEAD_NOT_FOUND");

        // Fan out all queries in parallel.
        var activitiesTask = db.LeadActivities
            .Where(a => a.LeadId == query.LeadId)
            .ToListAsync(ct);

        var scoresTask = db.ScoreHistories
            .Where(s => s.LeadId == query.LeadId)
            .ToListAsync(ct);

        var assignmentsTask = db.LeadAssignments
            .Where(a => a.LeadId == query.LeadId)
            .ToListAsync(ct);

        var remindersTask = db.LeadReminders
            .Where(r => r.LeadId == query.LeadId)
            .ToListAsync(ct);

        // Merges in which this lead participated (as target or as source).
        var mergesTask = db.LeadMerges
            .Where(m => m.TargetLeadId == query.LeadId || m.SourceLeadId == query.LeadId)
            .ToListAsync(ct);

        await Task.WhenAll(activitiesTask, scoresTask, assignmentsTask, remindersTask, mergesTask);

        var events = new List<TimelineEvent>();

        // Activities
        foreach (var a in activitiesTask.Result)
        {
            var detail = a.Outcome.HasValue
                ? $"{a.Notes} • Outcome: {a.Outcome}"
                : a.Notes;

            events.Add(new TimelineEvent(
                OccurredAt: a.PerformedAt,
                Kind:        TimelineEventKind.Activity,
                Title:       $"{a.Type}: {a.Subject}",
                Detail:      detail,
                ActorId:     a.PerformedBy));
        }

        // Score changes
        foreach (var s in scoresTask.Result)
        {
            events.Add(new TimelineEvent(
                OccurredAt: s.RecalculatedAt,
                Kind:        TimelineEventKind.ScoreChange,
                Title:       $"Score updated to {s.Score}",
                Detail:      s.TriggerEvent,
                ActorId:     null));
        }

        // Assignments
        foreach (var a in assignmentsTask.Result)
        {
            var title = a.WasManualOverride
                ? $"Manually assigned to agent {a.AgentId}"
                : $"Dispatched to agent {a.AgentId} via {a.Strategy}";

            var detail = a.FirstContactAt.HasValue
                ? $"First contact: {a.FirstContactAt:u}"
                : $"SLA deadline: {a.SlaDeadline:u}";

            events.Add(new TimelineEvent(
                OccurredAt: a.CreatedAt,
                Kind:        TimelineEventKind.Assignment,
                Title:       title,
                Detail:      detail,
                ActorId:     a.AgentId));
        }

        // Reminders — one event at creation, another at resolution if resolved
        foreach (var r in remindersTask.Result)
        {
            events.Add(new TimelineEvent(
                OccurredAt: r.CreatedAt,
                Kind:        TimelineEventKind.Reminder,
                Title:       $"Reminder created: {r.Title}",
                Detail:      r.Notes,
                ActorId:     r.CreatedBy));

            if (r.ResolvedAt.HasValue)
            {
                events.Add(new TimelineEvent(
                    OccurredAt: r.ResolvedAt.Value,
                    Kind:        TimelineEventKind.Reminder,
                    Title:       $"Reminder {r.Status.ToString().ToLowerInvariant()}: {r.Title}",
                    Detail:      null,
                    ActorId:     null));
            }
        }

        // Merge events
        foreach (var m in mergesTask.Result)
        {
            if (m.TargetLeadId == query.LeadId)
            {
                var overrides = string.IsNullOrEmpty(m.OverriddenFields)
                    ? string.Empty
                    : $" • Fields taken from source: {m.OverriddenFields}";

                events.Add(new TimelineEvent(
                    OccurredAt: m.MergedAt,
                    Kind:        TimelineEventKind.Merge,
                    Title:       $"Lead {m.SourceLeadId} merged into this lead",
                    Detail:      $"Merged by {m.MergedBy}{overrides}",
                    ActorId:     m.MergedBy));
            }
            else
            {
                events.Add(new TimelineEvent(
                    OccurredAt: m.MergedAt,
                    Kind:        TimelineEventKind.Merge,
                    Title:       $"This lead was merged into {m.TargetLeadId}",
                    Detail:      $"Merged by {m.MergedBy}",
                    ActorId:     m.MergedBy));
            }
        }

        return Result.Ok<IReadOnlyList<TimelineEvent>>(
            [.. events.OrderByDescending(e => e.OccurredAt)]);
    }
}
