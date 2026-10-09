namespace Sankore.Modules.Integration.Features.Mappings.ListMappings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class ListMappingsHandler(IntegrationDbContext db)
    : IRequestHandler<ListMappingsQuery, Result<ListMappingsResponse>>
{
    public async Task<Result<ListMappingsResponse>> Handle(
        ListMappingsQuery query, CancellationToken ct)
    {
        if (!await db.ConnectionExistsAsync(query.ConnectionId, ct))
            return Result.Fail<ListMappingsResponse>(IntegrationErrors.ConnectionNotFound);

        // Un-paginated on purpose: a correspondence table is a closed list an administrator
        // maintains by hand — tens of rows per domain, hundreds in total — and the screen needs
        // all of a domain at once to show what is missing next to what is mapped.
        var items = await db.Mappings
            .Where(m => m.ConnectionId == query.ConnectionId)
            .Where(m => query.Domain == null || m.Domain == query.Domain)
            .OrderBy(m => m.Domain)
            .ThenBy(m => m.CrmCode)
            .ToListAsync(ct);

        return Result.Ok(new ListMappingsResponse(
            items.Count, items.Select(m => m.ToDto()).ToList()));
    }
}
