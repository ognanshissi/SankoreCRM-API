namespace Sankore.Modules.Leads.Features.LogActivity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class LogActivityHandler(
    LeadsDbContext db,
    ISender sender,
    IOptions<LeadModuleSettings> settings)
    : IRequestHandler<LogActivityCommand, Result<LogActivityResult>>
{
    /// <summary>Default retention period for sensitive visit data (GPS + photo).</summary>
    private static readonly TimeSpan DefaultVisitRetention = TimeSpan.FromDays(365);

    public async Task<Result<LogActivityResult>> Handle(
        LogActivityCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<LogActivityResult>("LEAD_NOT_FOUND");

        // ── Visit consent check (US-M13-102) ────────────────────────────
        // GPS/photo are sensitive PII; E03 (LocationTracking) consent required.
        var hasLocationData = cmd.VisitLatitude.HasValue || cmd.VisitLongitude.HasValue
                           || cmd.VisitPhotoReference is not null;

        if (cmd.Type == ActivityType.Visit && hasLocationData)
        {
            var hasConsent = await db.LeadConsents
                .AnyAsync(c => c.LeadId == cmd.LeadId
                            && c.Type == ConsentType.LocationTracking
                            && c.Status == ConsentStatus.Active, ct);

            if (!hasConsent)
                return Result.Fail<LogActivityResult>("LOCATION_CONSENT_REQUIRED");
        }

        // ── Build visit location ────────────────────────────────────────
        GeoPoint? visitLocation = null;
        if (cmd.VisitLatitude.HasValue && cmd.VisitLongitude.HasValue)
            visitLocation = new GeoPoint(cmd.VisitLatitude.Value, cmd.VisitLongitude.Value);

        // ── Retention policy for sensitive visit data ────────────────────
        DateTimeOffset? retentionExpiresAt = null;
        if (hasLocationData)
            retentionExpiresAt = DateTimeOffset.UtcNow.Add(DefaultVisitRetention);

        var activity = LeadActivity.Create(
            tenantId:            lead.TenantId,
            leadId:              lead.Id,
            type:                cmd.Type,
            subject:             cmd.Subject,
            performedBy:         cmd.PerformedBy,
            notes:               cmd.Notes,
            scheduledAt:         cmd.ScheduledAt,
            durationMinutes:     cmd.DurationMinutes,
            outcome:             cmd.Outcome,
            attachmentsJson:     cmd.AttachmentsJson,
            ctiCallReference:    cmd.CtiCallReference,
            isSystemGenerated:   cmd.IsSystemGenerated,
            visitLocation:       visitLocation,
            visitPhotoReference: cmd.VisitPhotoReference,
            retentionExpiresAt:  retentionExpiresAt);

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
