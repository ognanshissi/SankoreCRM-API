using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Features.Territories.CreateTerritory;

internal sealed class CreateTerritoryHandler(
    AdministrationDbContext db,
    ITenantContext tenant
) : IRequestHandler<CreateTerritoryCommand, Result<CreateTerritoryResult>>
{
    public async Task<Result<CreateTerritoryResult>> Handle(
        CreateTerritoryCommand request, CancellationToken ct)
    {
        // Guard: code must be unique within tenant
        var codeTaken = await db.Territories
            .AnyAsync(t => t.Code == request.Code.ToUpperInvariant(), ct);

        if (codeTaken)
            return Result.Fail<CreateTerritoryResult>(
                $"A territory with code '{request.Code.ToUpperInvariant()}' already exists.");

        var location = request.Latitude.HasValue && request.Longitude.HasValue
            ? new GeoPoint(request.Latitude.Value, request.Longitude.Value)
            : null;

        var territory = Territory.Create(
            tenantId: tenant.CurrentTenantId,
            name: request.Name,
            code: request.Code,
            description: request.Description,
            location: location,
            rayonKm: request.RayonKm,
            productSpecialities: request.ProductSpecialities);

        await db.Territories.AddAsync(territory, ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new CreateTerritoryResult(territory.Id));
    }
}
