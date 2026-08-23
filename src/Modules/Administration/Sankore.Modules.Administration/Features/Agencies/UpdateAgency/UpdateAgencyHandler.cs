using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Features.Agencies.UpdateAgency;

internal sealed class UpdateAgencyHandler(
    AdministrationDbContext db
) : IRequestHandler<UpdateAgencyCommand, Result>
{
    public async Task<Result> Handle(UpdateAgencyCommand request, CancellationToken ct)
    {
        var agency = await db.Agencies
            .AsTracking()
            .Where(a => a.Id == request.AgencyId)
            .SingleOrDefaultAsync(ct);

        if (agency is null)
            return Result.Fail($"Agency {request.AgencyId} not found.");

        if (agency.IsDeleted)
            return Result.Fail("Cannot update a deleted agency.");

        Address? address = null;
        if (!string.IsNullOrWhiteSpace(request.AddressStreet) ||
            !string.IsNullOrWhiteSpace(request.AddressCity))
        {
            var location = request.Latitude.HasValue && request.Longitude.HasValue
                ? new GeoPoint(request.Latitude.Value, request.Longitude.Value)
                : null;

            address = Address.Create(
                request.AddressStreet ?? string.Empty,
                request.AddressCity ?? string.Empty,
                request.AddressState ?? string.Empty,
                request.AddressCountry ?? string.Empty,
                request.AddressZipCode ?? string.Empty,
                location);
        }

        agency.Update(request.Name, request.Description, request.AgencyType, address);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
