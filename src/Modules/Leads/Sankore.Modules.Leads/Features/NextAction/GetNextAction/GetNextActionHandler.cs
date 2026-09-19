namespace Sankore.Modules.Leads.Features.NextAction.GetNextAction;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetNextActionHandler(LeadsDbContext db)
    : IRequestHandler<GetNextActionQuery, Result<NextActionDto>>
{
    public async Task<Result<NextActionDto>> Handle(
        GetNextActionQuery query, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == query.LeadId, ct);

        if (lead is null)
            return Result.Fail<NextActionDto>("LEAD_NOT_FOUND");

        // Closed leads have no actionable next step.
        if (lead.Status is LeadStatus.Converted or LeadStatus.Archived
                        or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail<NextActionDto>("LEAD_IS_CLOSED");

        var now = DateTimeOffset.UtcNow;

        // Load the current assignment and pending reminders in parallel.
        var assignmentTask = lead.CurrentAssignmentId.HasValue
            ? db.LeadAssignments
                .AsNoTracking()
                .Where(a => a.Id == lead.CurrentAssignmentId.Value)
                .FirstOrDefaultAsync(ct)
            : Task.FromResult<LeadAssignment?>(null);

        var overdueReminderTask = db.LeadReminders
            .AsNoTracking()
            .Where(r => r.LeadId == lead.Id &&
                        r.Status == ReminderStatus.Pending &&
                        r.DueAt < now)
            .OrderBy(r => r.DueAt)
            .FirstOrDefaultAsync(ct);

        await Task.WhenAll(assignmentTask, overdueReminderTask);

        var assignment      = assignmentTask.Result;
        var overdueReminder = overdueReminderTask.Result;

        // ── Priority-ordered rule engine ─────────────────────────────────────

        // 1. SLA breach on active assignment
        if (assignment is not null && assignment.HasBreachedSla(now))
            return Result.Ok(Build(lead.Id, NextActionType.ContactAgain,
                "Contacter le lead — SLA dépassé",
                $"Aucun premier contact enregistré avant l'échéance SLA ({assignment.SlaDeadline:dd/MM/yyyy HH:mm}).",
                "High", now.AddHours(4)));

        // 2. Overdue reminder
        if (overdueReminder is not null)
            return Result.Ok(Build(lead.Id, NextActionType.FollowUp,
                $"Rappel en retard : {overdueReminder.Title}",
                $"Ce rappel était dû le {overdueReminder.DueAt:dd/MM/yyyy HH:mm}.",
                "High", now.AddHours(24)));

        // 3. Qualification form started but incomplete (< 50%)
        if (lead.QualificationCompleteness is > 0 and < 0.5 &&
            lead.Status is not LeadStatus.Qualified)
            return Result.Ok(Build(lead.Id, NextActionType.CompleteQualification,
                "Compléter le formulaire de qualification",
                $"Formulaire complété à {lead.QualificationCompleteness:P0} — au moins 50 % requis pour le dispatch.",
                "Medium", now.AddHours(48)));

        // 4. Never scored / New / Open with no score
        if (lead.Score == 0 && lead.Status is LeadStatus.New or LeadStatus.Open or LeadStatus.Recycled)
            return Result.Ok(Build(lead.Id, NextActionType.Qualify,
                "Qualifier le lead",
                "Le lead n'a pas encore été scoré. Lancer le formulaire de qualification.",
                "Medium", now.AddHours(48)));

        // 5. Qualified but not yet dispatched
        if (lead.Status == LeadStatus.Qualified && lead.CurrentAssignmentId is null)
            return Result.Ok(Build(lead.Id, NextActionType.Dispatch,
                "Dispatcher le lead à un agent",
                $"Score {lead.Score}/100 — le lead est éligible au dispatch.",
                "High", now.AddHours(24)));

        // 6. Qualifying: score 40-59 — collect more data
        if (lead.Score is >= 40 and < 60)
            return Result.Ok(Build(lead.Id, NextActionType.CollectMoreData,
                "Compléter les informations manquantes",
                $"Score actuel : {lead.Score}/100 — atteindre 60 pour être éligible au dispatch.",
                "Medium", now.AddHours(48)));

        // 7. No recent activity (> 14 days)
        if (lead.LastActivityAt.HasValue &&
            (now - lead.LastActivityAt.Value).TotalDays > 14)
            return Result.Ok(Build(lead.Id, NextActionType.FollowUp,
                "Reprendre contact",
                $"Dernière activité : {lead.LastActivityAt.Value:dd/MM/yyyy} ({(int)(now - lead.LastActivityAt.Value).TotalDays} jours).",
                "Medium", now.AddHours(48)));

        // 8. Nurturing
        if (lead.Status == LeadStatus.Nurturing)
            return Result.Ok(Build(lead.Id, NextActionType.Nurture,
                "Maintenir le contact — lead en nurturing",
                "Planifier un prochain point de contact pour maintenir la relation commerciale.",
                "Low", now.AddDays(7)));

        // 9. Score too low
        if (lead.Score is > 0 and < 40)
            return Result.Ok(Build(lead.Id, NextActionType.Disqualify,
                "Envisager la disqualification",
                $"Score {lead.Score}/100 — en dessous du seuil minimum (40). Vérifier les critères d'éligibilité.",
                "Low", now.AddDays(3)));

        // 10. Default — generic follow-up
        return Result.Ok(Build(lead.Id, NextActionType.FollowUp,
            "Effectuer un point de suivi",
            "Aucune action prioritaire identifiée. Maintenir un suivi régulier.",
            "Low", now.AddDays(3)));
    }

    private static NextActionDto Build(
        Guid leadId, NextActionType type, string title, string detail,
        string urgency, DateTimeOffset suggestedDueAt)
        => new(leadId, type, title, detail, urgency, suggestedDueAt);
}
