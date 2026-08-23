using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

internal sealed class CreateAgencyHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<CreateAgencyCommand, Result<CreateAgencyResult>>
{
    public async Task<Result<CreateAgencyResult>> Handle(
        CreateAgencyCommand request, CancellationToken ct)
    {
        // Guard: non-HQ agencies must reference an existing parent
        if (request.ParentAgencyId.HasValue)
        {
            var parentExists = await db.Agencies
                .AnyAsync(a => a.Id == request.ParentAgencyId.Value, ct);

            if (!parentExists)
                return Result.Fail<CreateAgencyResult>(
                    $"Parent agency {request.ParentAgencyId} not found.");
        }

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

        var agency = Agency.Create(
            tenantId: currentUser.TenantId,
            name: request.Name,
            description: request.Description,
            agencyType: request.AgencyType,
            parentAgencyId: request.ParentAgencyId,
            createdBy: currentUser.Id,
            address: address);

        await db.Agencies.AddAsync(agency, ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new CreateAgencyResult(agency.Id, agency.Code));
    }
}
