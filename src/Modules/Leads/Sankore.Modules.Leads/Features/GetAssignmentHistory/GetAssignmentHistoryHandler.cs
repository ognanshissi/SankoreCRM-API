namespace Sankore.Modules.Leads.Features.GetAssignmentHistory;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetAssignmentHistoryHandler(LeadsDbContext db)
    : IRequestHandler<GetAssignmentHistoryQuery, Result<IReadOnlyList<AssignmentDto>>>
{
    public async Task<Result<IReadOnlyList<AssignmentDto>>> Handle(
        GetAssignmentHistoryQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<AssignmentDto>>("LEAD_NOT_FOUND");

        var now = DateTimeOffset.UtcNow;

        var assignments = await db.LeadAssignments
            .Where(a => a.LeadId == query.LeadId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AssignmentDto(
                a.Id,
                a.AgentId,
                a.Strategy,
                a.CompatibilityScore,
                a.WasManualOverride,
                a.OverrideReason,
                a.CreatedAt,
                a.SlaDeadline,
                a.FirstContactAt,
                a.FirstContactAt == null && now > a.SlaDeadline))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<AssignmentDto>>(assignments);
    }
}
