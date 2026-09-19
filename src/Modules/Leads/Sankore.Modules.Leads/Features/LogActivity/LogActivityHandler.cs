namespace Sankore.Modules.Leads.Features.LogActivity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class LogActivityHandler(
    LeadsDbContext db,
    ISender sender,
    IOptions<LeadModuleSettings> settings)
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

        // Auto-recalculate score after the activity is persisted (if enabled and lead is active).
        if (settings.Value.EnableAutoScoreRecalculation &&
            lead.Status is not (LeadStatus.Converted or LeadStatus.Archived
                             or LeadStatus.Lost or LeadStatus.Disqualified))
        {
            await sender.Send(
                new RecalculateLeadScoreCommand(lead.Id, "ACTIVITY_LOGGED"), ct);
        }

        return Result.Ok(new LogActivityResult(activity.Id, activity.PerformedAt));
    }
}
