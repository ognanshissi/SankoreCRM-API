namespace Sankore.Modules.Leads.Features.Opportunities.ListOpportunities;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// The open projects past their expected close date, under the visibility of the projects list: the
/// number « Ma journée » shows is what « En retard » on that list opens on.
/// </summary>
internal sealed record GetOverdueProjectCountQuery : IRequest<Result<int>>;

internal sealed class GetOverdueProjectCountHandler(LeadsDbContext db, TimeProvider clock, LeadVisibility? visibility = null)
    : IRequestHandler<GetOverdueProjectCountQuery, Result<int>>
{
    public async Task<Result<int>> Handle(GetOverdueProjectCountQuery query, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var projects = visibility is null || visibility.SeesAll
            ? db.Opportunities.AsNoTracking()
            : visibility.Apply(db.Opportunities.AsNoTracking(), db.Leads);

        return Result.Ok(await projects.CountAsync(o =>
            o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost && o.ExpectedCloseDate < now, ct));
    }
}
