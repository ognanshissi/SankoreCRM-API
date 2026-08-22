using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Features.Territories.UpdateTerritory;

internal sealed class UpdateTerritoryHandler(
    AdministrationDbContext db
) : IRequestHandler<UpdateTerritoryCommand, Result>
{
    public async Task<Result> Handle(UpdateTerritoryCommand request, CancellationToken ct)
    {
        var territory = await db.Territories
            .AsTracking()
            .Where(t => t.Id == request.TerritoryId)
            .SingleOrDefaultAsync(ct);

        if (territory is null)
            return Result.Fail($"Territory {request.TerritoryId} not found.");

        if (!territory.IsActive)
            return Result.Fail("Cannot update a deactivated territory.");

        var location = request.Latitude.HasValue && request.Longitude.HasValue
            ? new GeoPoint(request.Latitude.Value, request.Longitude.Value)
            : null;

        territory.Update(
            name: request.Name,
            description: request.Description,
            location: location,
            rayonKm: request.RayonKm,
            productSpecialities: request.ProductSpecialities);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
