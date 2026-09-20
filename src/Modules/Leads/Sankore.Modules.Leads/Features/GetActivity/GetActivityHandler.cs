namespace Sankore.Modules.Leads.Features.GetActivity;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.ListActivities;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetActivityHandler(LeadsDbContext db)
    : IRequestHandler<GetActivityQuery, Result<ActivityDto>>
{
    public async Task<Result<ActivityDto>> Handle(
        GetActivityQuery query, CancellationToken ct)
    {
        var activity = await db.LeadActivities
            .Where(a => a.Id == query.ActivityId && a.LeadId == query.LeadId)
            .Select(a => new ActivityDto(
                a.Id,
                a.Type,
                a.Subject,
                a.Notes,
                a.PerformedBy,
                a.ScheduledAt,
                a.PerformedAt,
                a.DurationMinutes,
                a.Outcome,
                a.AttachmentsJson,
                a.CtiCallReference,
                a.IsSystemGenerated,
                a.VisitLocation != null ? a.VisitLocation.Latitude : null,
                a.VisitLocation != null ? a.VisitLocation.Longitude : null,
                a.VisitPhotoReference))
            .FirstOrDefaultAsync(ct);

        return activity is null
            ? Result.Fail<ActivityDto>("ACTIVITY_NOT_FOUND")
            : Result.Ok(activity);
    }
}
