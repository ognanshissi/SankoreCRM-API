namespace Sankore.Modules.Leads.Features.SlaConfigs.ListSlaConfigs;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListSlaConfigsHandler(LeadsDbContext db)
    : IRequestHandler<ListSlaConfigsQuery, Result<IReadOnlyList<SlaConfigDto>>>
{
    public async Task<Result<IReadOnlyList<SlaConfigDto>>> Handle(
        ListSlaConfigsQuery query, CancellationToken ct)
    {
        var q = db.SlaConfigs.AsQueryable();

        if (query.ActiveOnly == true)
            q = q.Where(s => s.IsActive);

        if (query.AgencyId is not null)
            q = q.Where(s => s.AgencyId == query.AgencyId);

        var configs = await q
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SlaConfigDto(
                s.Id,
                s.AgencyId,
                s.Name,
                s.FirstContactDeadline,
                s.QualificationDeadline,
                s.FollowUpDeadline,
                s.EscalationDeadline,
                s.IsActive,
                s.CreatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<SlaConfigDto>>(configs);
    }
}
