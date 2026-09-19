namespace Sankore.Modules.Leads.Features.GetOwnerHistory;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetOwnerHistoryHandler(LeadsDbContext db)
    : IRequestHandler<GetOwnerHistoryQuery, Result<IReadOnlyList<OwnerAssignmentDto>>>
{
    public async Task<Result<IReadOnlyList<OwnerAssignmentDto>>> Handle(
        GetOwnerHistoryQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<OwnerAssignmentDto>>("LEAD_NOT_FOUND");

        var history = await db.LeadOwnerAssignmentHistories
            .AsNoTracking()
            .Where(h => h.LeadId == query.LeadId)
            .OrderByDescending(h => h.AssignedAt)
            .Select(h => new OwnerAssignmentDto(
                h.Id,
                h.PreviousOwnerId,
                h.NewOwnerId,
                h.AssignmentMethod,
                h.Reason,
                h.AssignedBy,
                h.AssignedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<OwnerAssignmentDto>>(history);
    }
}
