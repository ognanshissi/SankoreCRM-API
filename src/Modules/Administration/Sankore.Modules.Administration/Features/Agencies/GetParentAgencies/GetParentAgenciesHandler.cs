using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetParentAgencies;

internal sealed class GetParentAgenciesHandler(
    AdministrationDbContext db
) : IRequestHandler<GetParentAgenciesQuery, Result<List<AgencyDto>>>
{
    public async Task<Result<List<AgencyDto>>> Handle(GetParentAgenciesQuery request, CancellationToken ct)
    {
        var items = await db.Agencies
            .Where(a => !a.IsDeleted && a.ParentAgencyId == null)
            .OrderBy(a => a.Name)
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
            .ToListAsync(ct);

        return Result.Ok(items);
    }
}
