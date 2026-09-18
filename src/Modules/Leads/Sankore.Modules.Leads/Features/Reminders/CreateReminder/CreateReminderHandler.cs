namespace Sankore.Modules.Leads.Features.Reminders.CreateReminder;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateReminderHandler(LeadsDbContext db)
    : IRequestHandler<CreateReminderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateReminderCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<Guid>("LEAD_NOT_FOUND");

        var reminder = LeadReminder.Create(
            tenantId:  lead.TenantId,
            leadId:    lead.Id,
            title:     cmd.Title,
            createdBy: cmd.CreatedBy,
            dueAt:     cmd.DueAt,
            notes:     cmd.Notes);

        db.LeadReminders.Add(reminder);
        await db.SaveChangesAsync(ct);

        return Result.Ok(reminder.Id);
    }
}
