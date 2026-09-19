namespace Sankore.Modules.Leads.Features.NextAction.AcknowledgeNextAction;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.NextAction.GetNextAction;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class AcknowledgeNextActionHandler(LeadsDbContext db, ISender sender)
    : IRequestHandler<AcknowledgeNextActionCommand, Result<AcknowledgeNextActionResult>>
{
    public async Task<Result<AcknowledgeNextActionResult>> Handle(
        AcknowledgeNextActionCommand cmd, CancellationToken ct)
    {
        // Validate lead exists and is active.
        var lead = await db.Leads
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<AcknowledgeNextActionResult>("LEAD_NOT_FOUND");

        if (lead.Status is LeadStatus.Converted or LeadStatus.Archived
                        or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail<AcknowledgeNextActionResult>("LEAD_IS_CLOSED");

        // Ignore: no side effects.
        if (cmd.Action == NextActionAcknowledgement.Ignore)
            return Result.Ok(new AcknowledgeNextActionResult(cmd.LeadId, cmd.Action, null));

        // Accept / Reschedule: resolve the due date, then compute the suggested
        // action title to carry into the reminder for traceability.
        var nextActionResult = await sender.Send(new GetNextActionQuery(cmd.LeadId), ct);
        if (nextActionResult.IsFailure)
            return Result.Fail<AcknowledgeNextActionResult>(nextActionResult.Error!);

        var suggestion = nextActionResult.Value!;

        DateTimeOffset dueAt;
        if (cmd.Action == NextActionAcknowledgement.Reschedule)
        {
            if (!cmd.RescheduledAt.HasValue)
                return Result.Fail<AcknowledgeNextActionResult>("RESCHEDULE_DATE_REQUIRED");

            if (cmd.RescheduledAt.Value <= DateTimeOffset.UtcNow)
                return Result.Fail<AcknowledgeNextActionResult>("RESCHEDULE_DATE_MUST_BE_FUTURE");

            dueAt = cmd.RescheduledAt.Value;
        }
        else // Accept
        {
            dueAt = suggestion.SuggestedDueAt;
        }

        var reminder = LeadReminder.Create(
            tenantId:  lead.TenantId,
            leadId:    lead.Id,
            title:     suggestion.Title,
            createdBy: cmd.AcknowledgedBy,
            dueAt:     dueAt,
            notes:     suggestion.Detail);

        db.LeadReminders.Add(reminder);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new AcknowledgeNextActionResult(cmd.LeadId, cmd.Action, reminder.Id));
    }
}
