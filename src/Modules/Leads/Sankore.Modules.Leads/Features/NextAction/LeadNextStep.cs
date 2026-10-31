namespace Sankore.Modules.Leads.Features.NextAction;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>Where a lead's next step comes from.</summary>
public enum NextStepKind
{
    /// <summary>An open CRM task on the lead — its own due date.</summary>
    Task,
    /// <summary>A pending reminder on the lead — its own due date.</summary>
    Reminder,
    /// <summary>The current assignment's first contact, due at its SLA deadline.</summary>
    FirstContact,
    /// <summary>Nothing is planned: the recommendation engine's suggestion, with no due date.</summary>
    Suggestion
}

/// <summary>
/// The one thing to do next on a lead, and when.
/// </summary>
/// <param name="DueAt">
/// A date somebody committed to — a task, a reminder, an SLA. Null for a <see cref="NextStepKind.Suggestion"/>:
/// nothing is planned, and saying so is the point. The recommendation engine used to answer
/// "now + 48 h" on every read, a date that moved forward each time the lead was opened and so could
/// never be late.
/// </param>
/// <param name="AssigneeId">Who the step is on: the task's agent, the assigned agent for a first contact.</param>
/// <param name="ActionType">The engine's action, for a suggestion only.</param>
/// <param name="Urgency">High / Medium / Low, for a suggestion only.</param>
public sealed record LeadNextStepDto(
    NextStepKind Kind,
    string Title,
    string? Detail,
    DateTimeOffset? DueAt,
    bool IsOverdue,
    Guid? TaskId = null,
    Guid? ReminderId = null,
    Guid? AssigneeId = null,
    NextActionType? ActionType = null,
    string? Urgency = null);

/// <summary>What the next-step rules read from a lead — enough to build it from a list row.</summary>
internal sealed record LeadFacts(
    Guid Id,
    LeadStatus Status,
    int Score,
    double QualificationCompleteness,
    DateTimeOffset? LastActivityAt,
    Guid? CurrentAssignmentId,
    Guid? CurrentAssignedId);

internal sealed record NextActionSuggestion(
    NextActionType Type,
    string Title,
    string Detail,
    string Urgency,
    TimeSpan SuggestedDelay);

/// <summary>
/// The recommendation rules that read only the lead itself — rules 3 to 10 of the next-action
/// engine. The SLA and overdue-reminder rules that came before them are commitments, and are
/// answered by <see cref="LeadNextSteps"/> with their real dates.
/// </summary>
internal static class NextActionRules
{
    public static bool IsClosed(LeadStatus status) =>
        status is LeadStatus.Converted or LeadStatus.Archived or LeadStatus.Lost or LeadStatus.Disqualified;

    public static NextActionSuggestion Suggest(LeadFacts lead, DateTimeOffset now)
    {
        // Qualification form started but incomplete (< 50%)
        if (lead.QualificationCompleteness is > 0 and < 0.5 && lead.Status is not LeadStatus.Qualified)
            return new(NextActionType.CompleteQualification,
                "Compléter le formulaire de qualification",
                $"Formulaire complété à {lead.QualificationCompleteness:P0} — au moins 50 % requis pour le dispatch.",
                "Medium", TimeSpan.FromHours(48));

        // Not yet qualified. Read off the status, not a zero score: a lead is scored at its capture
        // now (lead temperature, lot 2), and qualifying it is still the agent's next step.
        if (lead.Status is LeadStatus.New or LeadStatus.Open or LeadStatus.Recycled)
            return new(NextActionType.Qualify,
                "Qualifier le lead",
                "Le lead n'a pas encore été qualifié. Lancer le formulaire de qualification.",
                "Medium", TimeSpan.FromHours(48));

        // Qualified but not yet dispatched
        if (lead.Status == LeadStatus.Qualified && lead.CurrentAssignmentId is null)
            return new(NextActionType.Dispatch,
                "Dispatcher le lead à un agent",
                $"Score {lead.Score}/100 — le lead est éligible au dispatch.",
                "High", TimeSpan.FromHours(24));

        // Qualifying: score 40-59 — collect more data
        if (lead.Score is >= 40 and < 60)
            return new(NextActionType.CollectMoreData,
                "Compléter les informations manquantes",
                $"Score actuel : {lead.Score}/100 — atteindre 60 pour être éligible au dispatch.",
                "Medium", TimeSpan.FromHours(48));

        // No recent activity (> 14 days)
        if (lead.LastActivityAt.HasValue && (now - lead.LastActivityAt.Value).TotalDays > 14)
            return new(NextActionType.FollowUp,
                "Reprendre contact",
                $"Dernière activité : {lead.LastActivityAt.Value:dd/MM/yyyy} ({(int)(now - lead.LastActivityAt.Value).TotalDays} jours).",
                "Medium", TimeSpan.FromHours(48));

        if (lead.Status == LeadStatus.Nurturing)
            return new(NextActionType.Nurture,
                "Maintenir le contact — lead en nurturing",
                "Planifier un prochain point de contact pour maintenir la relation commerciale.",
                "Low", TimeSpan.FromDays(7));

        if (lead.Score is > 0 and < 40)
            return new(NextActionType.Disqualify,
                "Envisager la disqualification",
                $"Score {lead.Score}/100 — en dessous du seuil minimum (40). Vérifier les critères d'éligibilité.",
                "Low", TimeSpan.FromDays(3));

        return new(NextActionType.FollowUp,
            "Effectuer un point de suivi",
            "Aucune action prioritaire identifiée. Maintenir un suivi régulier.",
            "Low", TimeSpan.FromDays(3));
    }
}

/// <summary>
/// Resolves each lead's next step: the EARLIEST open commitment — task, pending reminder, or the
/// assignment's first contact while it is still owed — else the engine's suggestion, unplanned.
///
/// <para>
/// One resolver for the lead screen and the list, so the two cannot disagree about the same lead.
/// Batched: three queries whatever the page size, over the leads of the page only.
/// </para>
/// </summary>
internal static class LeadNextSteps
{
    private static readonly CrmTaskStatus[] OpenTask = [CrmTaskStatus.Pending, CrmTaskStatus.InProgress];

    public static async Task<IReadOnlyDictionary<Guid, LeadNextStepDto>> ForLeadsAsync(
        LeadsDbContext db, IReadOnlyCollection<LeadFacts> leads, DateTimeOffset now, CancellationToken ct)
    {
        var open = leads.Where(l => !NextActionRules.IsClosed(l.Status)).ToList();
        if (open.Count == 0) return new Dictionary<Guid, LeadNextStepDto>();

        // A List, not an array: on .NET 10 an array's Contains binds to the span extension.
        var leadIds = open.Select(l => l.Id).ToList();
        var assignmentIds = open
            .Where(l => l.CurrentAssignmentId.HasValue)
            .Select(l => l.CurrentAssignmentId!.Value)
            .ToList();

        var tasks = await db.CrmTasks
            .AsNoTracking()
            .Where(t => t.LeadId != null && leadIds.Contains(t.LeadId.Value) && OpenTask.Contains(t.Status))
            .Select(t => new { t.Id, LeadId = t.LeadId!.Value, t.Title, t.Type, t.DueAt, t.AssignedAgentId })
            .ToListAsync(ct);

        var reminders = await db.LeadReminders
            .AsNoTracking()
            .Where(r => leadIds.Contains(r.LeadId) && r.Status == ReminderStatus.Pending)
            .Select(r => new { r.Id, r.LeadId, r.Title, r.Notes, r.DueAt })
            .ToListAsync(ct);

        var owedFirstContacts = assignmentIds.Count == 0
            ? []
            : await db.LeadAssignments
                .AsNoTracking()
                .Where(a => assignmentIds.Contains(a.Id) && a.FirstContactAt == null && a.SupersededAt == null)
                .Select(a => new { a.LeadId, a.AgentId, a.SlaDeadline })
                .ToListAsync(ct);

        var tasksByLead = tasks.ToLookup(t => t.LeadId);
        var remindersByLead = reminders.ToLookup(r => r.LeadId);
        var firstContactByLead = owedFirstContacts.ToDictionary(a => a.LeadId);

        var result = new Dictionary<Guid, LeadNextStepDto>(open.Count);
        foreach (var lead in open)
        {
            var candidates = new List<LeadNextStepDto>();

            foreach (var t in tasksByLead[lead.Id])
                candidates.Add(new LeadNextStepDto(
                    NextStepKind.Task, t.Title, null, t.DueAt, t.DueAt < now,
                    TaskId: t.Id, AssigneeId: t.AssignedAgentId));

            foreach (var r in remindersByLead[lead.Id])
                candidates.Add(new LeadNextStepDto(
                    NextStepKind.Reminder, r.Title, r.Notes, r.DueAt, r.DueAt < now, ReminderId: r.Id));

            // The dispatch already gives the agent a "Premier contact" task due at the same SLA:
            // when it is open, it IS this step, and listing both would show the same call twice.
            if (firstContactByLead.TryGetValue(lead.Id, out var owed)
                && !tasksByLead[lead.Id].Any(t => t.Type == CrmTaskType.FirstContact))
            {
                candidates.Add(new LeadNextStepDto(
                    NextStepKind.FirstContact, "Premier contact",
                    "Le lead attend son premier appel depuis son attribution.",
                    owed.SlaDeadline, owed.SlaDeadline < now, AssigneeId: owed.AgentId));
            }

            if (candidates.Count > 0)
            {
                // Earliest due first; on a tie a task wins, it is what the agent's list shows.
                result[lead.Id] = candidates
                    .OrderBy(c => c.DueAt)
                    .ThenBy(c => c.Kind)
                    .First();
                continue;
            }

            var suggestion = NextActionRules.Suggest(lead, now);
            result[lead.Id] = new LeadNextStepDto(
                NextStepKind.Suggestion, suggestion.Title, suggestion.Detail, DueAt: null, IsOverdue: false,
                AssigneeId: lead.CurrentAssignedId,
                ActionType: suggestion.Type, Urgency: suggestion.Urgency);
        }

        return result;
    }

    public static LeadFacts FactsOf(Lead lead) => new(
        lead.Id, lead.Status, lead.Score, lead.QualificationCompleteness,
        lead.LastActivityAt, lead.CurrentAssignmentId, lead.CurrentAssignedId);
}
