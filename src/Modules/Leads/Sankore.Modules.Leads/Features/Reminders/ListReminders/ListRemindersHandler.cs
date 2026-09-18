namespace Sankore.Modules.Leads.Features.Reminders.ListReminders;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListRemindersHandler(LeadsDbContext db)
    : IRequestHandler<ListRemindersQuery, Result<IReadOnlyList<ReminderDto>>>
{
    public async Task<Result<IReadOnlyList<ReminderDto>>> Handle(
        ListRemindersQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<ReminderDto>>("LEAD_NOT_FOUND");

        var now = DateTimeOffset.UtcNow;

        var q = db.LeadReminders.Where(r => r.LeadId == query.LeadId);

        if (query.Status.HasValue)
            q = q.Where(r => r.Status == query.Status.Value);

        var reminders = await q
            .OrderBy(r => r.DueAt)
            .Select(r => new ReminderDto(
                r.Id,
                r.Title,
                r.Notes,
                r.DueAt,
                r.CreatedBy,
                r.Status,
                r.CreatedAt,
                r.ResolvedAt,
                r.Status == Domain.ReminderStatus.Pending && now > r.DueAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ReminderDto>>(reminders);
    }
}
