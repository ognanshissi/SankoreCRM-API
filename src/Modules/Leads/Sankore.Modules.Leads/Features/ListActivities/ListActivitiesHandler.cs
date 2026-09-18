namespace Sankore.Modules.Leads.Features.ListActivities;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListActivitiesHandler(LeadsDbContext db)
    : IRequestHandler<ListActivitiesQuery, Result<IReadOnlyList<ActivityDto>>>
{
    public async Task<Result<IReadOnlyList<ActivityDto>>> Handle(
        ListActivitiesQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<ActivityDto>>("LEAD_NOT_FOUND");

        var activities = await db.LeadActivities
            .Where(a => a.LeadId == query.LeadId)
            .OrderByDescending(a => a.PerformedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(a => new ActivityDto(
                a.Id,
                a.Type,
                a.Subject,
                a.Notes,
                a.PerformedBy,
                a.ScheduledAt,
                a.PerformedAt,
                a.DurationMinutes,
                a.Outcome))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ActivityDto>>(activities);
    }
}
