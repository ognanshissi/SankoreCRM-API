namespace Sankore.Modules.Leads.Features.Reminders.RescheduleReminder;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RescheduleReminderHandler(LeadsDbContext db)
    : IRequestHandler<RescheduleReminderCommand, Result>
{
    public async Task<Result> Handle(RescheduleReminderCommand cmd, CancellationToken ct)
    {
        var reminder = await db.LeadReminders
            .AsTracking()
            .FirstOrDefaultAsync(
                r => r.Id == cmd.ReminderId && r.LeadId == cmd.LeadId, ct);

        if (reminder is null)
            return Result.Fail("REMINDER_NOT_FOUND");

        var result = reminder.Reschedule(cmd.NewDueAt);
        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
