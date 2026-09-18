namespace Sankore.Modules.Leads.Features.Reminders.CompleteReminder;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CompleteReminderHandler(LeadsDbContext db)
    : IRequestHandler<CompleteReminderCommand, Result>
{
    public async Task<Result> Handle(
        CompleteReminderCommand cmd, CancellationToken ct)
    {
        var reminder = await db.LeadReminders
            .AsTracking()
            .FirstOrDefaultAsync(
                r => r.Id == cmd.ReminderId && r.LeadId == cmd.LeadId, ct);

        if (reminder is null)
            return Result.Fail("REMINDER_NOT_FOUND");

        var result = reminder.Complete();
        if (result.IsFailure)
            return result;

        // Completing a reminder counts as an activity on the lead.
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        lead?.RecordActivity();

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
