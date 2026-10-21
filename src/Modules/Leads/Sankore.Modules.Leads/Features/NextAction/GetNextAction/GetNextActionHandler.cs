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
        if (NextActionRules.IsClosed(lead.Status))
            return Result.Fail<NextActionDto>("LEAD_IS_CLOSED");

        var now = DateTimeOffset.UtcNow;

        var assignment = lead.CurrentAssignmentId.HasValue
            ? await db.LeadAssignments
                .AsNoTracking()
                .Where(a => a.Id == lead.CurrentAssignmentId.Value)
                .FirstOrDefaultAsync(ct)
            : null;

        var overdueReminder = await db.LeadReminders
            .AsNoTracking()
            .Where(r => r.LeadId == lead.Id &&
                        r.Status == ReminderStatus.Pending &&
                        r.DueAt < now)
            .OrderBy(r => r.DueAt)
            .FirstOrDefaultAsync(ct);

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

        // 3–10. Rules that read the lead alone — shared with the next step of the lead list.
        var suggestion = NextActionRules.Suggest(LeadNextSteps.FactsOf(lead), now);
        return Result.Ok(Build(lead.Id, suggestion.Type, suggestion.Title, suggestion.Detail,
            suggestion.Urgency, now.Add(suggestion.SuggestedDelay)));
    }

    private static NextActionDto Build(
        Guid leadId, NextActionType type, string title, string detail,
        string urgency, DateTimeOffset suggestedDueAt)
        => new(leadId, type, title, detail, urgency, suggestedDueAt);
}
