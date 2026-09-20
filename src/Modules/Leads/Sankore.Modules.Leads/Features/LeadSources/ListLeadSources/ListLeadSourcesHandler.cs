namespace Sankore.Modules.Leads.Features.LeadSources.ListLeadSources;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListLeadSourcesHandler(LeadsDbContext db)
    : IRequestHandler<ListLeadSourcesQuery, Result<IReadOnlyList<LeadSourceDto>>>
{
    public async Task<Result<IReadOnlyList<LeadSourceDto>>> Handle(
        ListLeadSourcesQuery query, CancellationToken ct)
    {
        var q = db.LeadSourceConfigs.AsQueryable();

        if (query.ActiveOnly == true)
            q = q.Where(s => s.IsActive);

        var sources = await q
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Code)
            .Select(s => new LeadSourceDto(
                s.Id, s.TenantId, s.Code, s.Label, s.Description,
                s.IsActive, s.IsSystem, s.DisplayOrder, s.CreatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<LeadSourceDto>>(sources);
    }
}
