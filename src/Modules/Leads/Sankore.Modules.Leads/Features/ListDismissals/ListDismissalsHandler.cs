namespace Sankore.Modules.Leads.Features.ListDismissals;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListDismissalsHandler(LeadsDbContext db)
    : IRequestHandler<ListDismissalsQuery, Result<IReadOnlyList<DismissalDto>>>
{
    public async Task<Result<IReadOnlyList<DismissalDto>>> Handle(
        ListDismissalsQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<DismissalDto>>("LEAD_NOT_FOUND");

        var dismissals = await db.DuplicateDismissals
            .Where(d => d.LeadId == query.LeadId || d.CandidateLeadId == query.LeadId)
            .OrderByDescending(d => d.DismissedAt)
            .Select(d => new DismissalDto(
                d.Id,
                d.LeadId,
                d.CandidateLeadId,
                d.DismissedBy,
                d.DismissedAt,
                d.Reason))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<DismissalDto>>(dismissals);
    }
}
