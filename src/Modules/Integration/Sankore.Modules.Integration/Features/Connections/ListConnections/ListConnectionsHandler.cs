namespace Sankore.Modules.Integration.Features.Connections.ListConnections;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListConnectionsHandler(IntegrationDbContext db)
    : IRequestHandler<ListConnectionsQuery, Result<PagedResult<ConnectionListDto>>>
{
    private const int MaxPageSize = 200;

    public async Task<Result<PagedResult<ConnectionListDto>>> Handle(
        ListConnectionsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        // No tenant predicate: the DbContext's global query filter scopes this, so another
        // tenant's connections simply are not in the result set.
        var rows = db.Connections.AsQueryable();

        if (query.Family is not null)
            rows = rows.Where(c => c.Family == query.Family);

        if (query.IsActive is not null)
            rows = rows.Where(c => c.IsActive == query.IsActive);

        var total = await rows.CountAsync(ct);

        // Active first, then by family and name: the screen's first question is "what is live".
        var items = await rows
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.Family)
            .ThenBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Result.Ok(new PagedResult<ConnectionListDto>(
            items.Select(ConnectionListDto.From).ToList(), total, page, pageSize));
    }
}
