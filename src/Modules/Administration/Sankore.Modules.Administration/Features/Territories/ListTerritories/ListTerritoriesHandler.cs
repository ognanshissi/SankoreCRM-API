using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Territories;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.ListTerritories;

internal sealed class ListTerritoriesHandler(
    AdministrationDbContext db
) : IRequestHandler<ListTerritoriesQuery, Result<List<TerritoryDto>>>
{
    public async Task<Result<List<TerritoryDto>>> Handle(
        ListTerritoriesQuery request, CancellationToken ct)
    {
        var query = db.Territories.AsQueryable();

        if (!request.IncludeInactive)
            query = query.Where(t => t.IsActive);

        var territories = await query
            .OrderBy(t => t.Name)
            .Select(t => new TerritoryDto(
                t.Id,
                t.Name,
                t.Code,
                t.Description,
                t.Location != null ? t.Location.Latitude : (double?)null,
                t.Location != null ? t.Location.Longitude : (double?)null,
                t.RayonKm,
                t.ProductSpecialities,
                t.IsActive,
                t.CreatedAt,
                t.UpdatedAt))
            .ToListAsync(ct);

        return Result.Ok(territories);
    }
}
