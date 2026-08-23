using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetAgency;

internal sealed class GetAgencyHandler(
    AdministrationDbContext db
) : IRequestHandler<GetAgencyQuery, Result<AgencyDto>>
{
    public async Task<Result<AgencyDto>> Handle(GetAgencyQuery request, CancellationToken ct)
    {
        var dto = await db.Agencies
            .Where(a => a.Id == request.AgencyId)
            .Select(a => new AgencyDto(
                a.Id,
                a.Name,
                a.Code,
                a.Description,
                a.AgencyType.ToString(),
                a.ParentAgencyId,
                a.IsHeadQuarterAgency,
                a.IsActive,
                a.Address != null ? a.Address.Street : null,
                a.Address != null ? a.Address.City : null,
                a.Address != null ? a.Address.State : null,
                a.Address != null ? a.Address.Country : null,
                a.Address != null ? a.Address.ZipCode : null,
                a.Address != null && a.Address.Location != null ? a.Address.Location.Latitude : (double?)null,
                a.Address != null && a.Address.Location != null ? a.Address.Location.Longitude : (double?)null,
                a.CreatedAt,
                a.UpdatedAt))
            .SingleOrDefaultAsync(ct);

        return dto is null
            ? Result.Fail<AgencyDto>($"Agency {request.AgencyId} not found.")
            : Result.Ok(dto);
    }
}
