namespace Sankore.Modules.Leads.Features.LogActivity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class LogActivityHandler(LeadsDbContext db)
    : IRequestHandler<LogActivityCommand, Result<LogActivityResult>>
{
    public async Task<Result<LogActivityResult>> Handle(
        LogActivityCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<LogActivityResult>("LEAD_NOT_FOUND");

        var activity = LeadActivity.Create(
            tenantId:        lead.TenantId,
            leadId:          lead.Id,
            type:            cmd.Type,
            subject:         cmd.Subject,
            performedBy:     cmd.PerformedBy,
            notes:           cmd.Notes,
            scheduledAt:     cmd.ScheduledAt,
            durationMinutes: cmd.DurationMinutes,
            outcome:         cmd.Outcome);

        lead.RecordActivity();

        db.LeadActivities.Add(activity);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new LogActivityResult(activity.Id, activity.PerformedAt));
    }
}
