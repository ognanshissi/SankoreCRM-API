using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Territories;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Territories.GetTerritory;

internal sealed class GetTerritoryHandler(
    AdministrationDbContext db
) : IRequestHandler<GetTerritoryQuery, Result<TerritoryDto>>
{
    public async Task<Result<TerritoryDto>> Handle(
        GetTerritoryQuery request, CancellationToken ct)
    {
        var territory = await db.Territories
            .Where(t => t.Id == request.TerritoryId)
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
            .SingleOrDefaultAsync(ct);

        return territory is null
            ? Result.Fail<TerritoryDto>($"Territory {request.TerritoryId} not found.")
            : Result.Ok(territory);
    }
}
