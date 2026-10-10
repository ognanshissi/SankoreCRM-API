namespace Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class ListUnmappedCodesHandler(
    IntegrationDbContext db,
    ITenantContext tenant,
    ICrmCodeCatalog catalog)
    : IRequestHandler<ListUnmappedCodesQuery, Result<UnmappedCodesResponse>>
{
    public async Task<Result<UnmappedCodesResponse>> Handle(
        ListUnmappedCodesQuery query, CancellationToken ct)
    {
        if (!await db.ConnectionExistsAsync(query.ConnectionId, ct))
            return Result.Fail<UnmappedCodesResponse>(IntegrationErrors.ConnectionNotFound);

        var mapped = await db.Mappings
            .Where(m => m.ConnectionId == query.ConnectionId && m.Domain == query.Domain)
            .Select(m => m.CrmCode)
            .ToListAsync(ct);

        var list = await catalog.GetAsync(tenant.CurrentTenantId, query.Domain, ct);

        // Ordinal: the unique index is on the raw CRM code and the domain trims but never
        // case-folds, so an "Abidjan" mapping does not cover an "ABIDJAN" code. Comparing
        // case-insensitively here would hide exactly the row that is going to fail at dispatch.
        var mappedSet = new HashSet<string>(mapped, StringComparer.Ordinal);

        var unmapped = list.Codes
            .Where(c => !mappedSet.Contains(c.Code))
            .Select(c => new UnmappedCrmCode(c.Code, c.Label))
            .ToList();

        return Result.Ok(new UnmappedCodesResponse(
            query.Domain.ToString(),
            list.Availability.ToString(),
            list.Source,
            list.Codes.Count,
            mapped.Count,
            unmapped));
    }
}
