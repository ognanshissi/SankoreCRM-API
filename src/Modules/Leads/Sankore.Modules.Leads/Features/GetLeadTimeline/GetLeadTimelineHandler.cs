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

        var mergesTask = db.LeadMerges
            .Where(m => m.TargetLeadId == query.LeadId || m.SourceLeadId == query.LeadId)
            .ToListAsync(ct);

        var dismissalsTask = db.DuplicateDismissals
            .Where(d => d.LeadId == query.LeadId || d.CandidateLeadId == query.LeadId)
            .ToListAsync(ct);

        var consentsTask = db.LeadConsents
            .Where(c => c.LeadId == query.LeadId)
            .ToListAsync(ct);

        // Qualification responses joined with template metadata for rich context.
        var qualificationsTask = (
            from r in db.QualificationResponses
            where r.LeadId == query.LeadId
            join t in db.QualificationTemplates on r.TemplateId equals t.Id
            select new
            {
                r.Id,
                r.AnsweredBy,
                r.AnsweredAt,
                r.ComputedScore,
                TemplateName = t.Name,
                t.ProductType
            }).ToListAsync(ct);

        await Task.WhenAll(
            activitiesTask, scoresTask, assignmentsTask, remindersTask,
            mergesTask, dismissalsTask, consentsTask, qualificationsTask);

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

        // Score changes — exclude entries tied to a qualification response
        // (those are surfaced as richer Qualification events below).
        var qualificationResponseIds = qualificationsTask.Result.Select(q => q.Id).ToHashSet();
        foreach (var s in scoresTask.Result.Where(s => s.QualificationResponseId is null ||
                                                        !qualificationResponseIds.Contains(s.QualificationResponseId.Value)))
        {
            events.Add(new TimelineEvent(
                OccurredAt: s.RecalculatedAt,
                Kind:        TimelineEventKind.ScoreChange,
                Title:       $"Score updated to {s.Score}",
                Detail:      s.TriggerEvent,
                ActorId:     null));
        }

        // Qualification form submissions
        foreach (var q in qualificationsTask.Result)
        {
            var product = q.ProductType.HasValue ? $" ({q.ProductType})" : string.Empty;
            events.Add(new TimelineEvent(
                OccurredAt: q.AnsweredAt,
                Kind:        TimelineEventKind.Qualification,
                Title:       $"Qualification via «{q.TemplateName}»{product} — {q.ComputedScore}/100",
                Detail:      NextActionLabel(q.ComputedScore),
                ActorId:     q.AnsweredBy));
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

        // Reminders
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

        // Consent events
        foreach (var c in consentsTask.Result)
        {
            events.Add(new TimelineEvent(
                OccurredAt: c.GrantedAt,
                Kind:        TimelineEventKind.Consent,
                Title:       $"Consent granted: {c.Type}",
                Detail:      $"Channel: {c.Channel}" + (c.ProofReference is not null ? $" • Proof: {c.ProofReference}" : string.Empty),
                ActorId:     c.RecordedBy));

            if (c.WithdrawnAt.HasValue)
            {
                events.Add(new TimelineEvent(
                    OccurredAt: c.WithdrawnAt.Value,
                    Kind:        TimelineEventKind.Consent,
                    Title:       $"Consent withdrawn: {c.Type}",
                    Detail:      c.WithdrawalReason,
                    ActorId:     c.WithdrawnBy));
            }
        }

        // Dismissal events
        foreach (var d in dismissalsTask.Result)
        {
            var otherLeadId = d.LeadId == query.LeadId ? d.CandidateLeadId : d.LeadId;
            var detail = string.IsNullOrEmpty(d.Reason) ? null : $"Reason: {d.Reason}";

            events.Add(new TimelineEvent(
                OccurredAt: d.DismissedAt,
                Kind:        TimelineEventKind.DuplicateDismissed,
                Title:       $"Lead {otherLeadId} marked as non-duplicate",
                Detail:      detail,
                ActorId:     d.DismissedBy));
        }

        return Result.Ok<IReadOnlyList<TimelineEvent>>(
            [.. events.OrderByDescending(e => e.OccurredAt)]);
    }

    private static string NextActionLabel(int score) => score switch
    {
        >= 60 => "Prêt à être dispatché à un agent commercial.",
        >= 40 => "Score insuffisant — compléter les informations manquantes.",
        _     => "Score trop faible — envisager la disqualification."
    };
}
