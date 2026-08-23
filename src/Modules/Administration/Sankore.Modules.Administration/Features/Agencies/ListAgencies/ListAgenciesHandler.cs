using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ListAgencies;

internal sealed class ListAgenciesHandler(
    AdministrationDbContext db
) : IRequestHandler<ListAgenciesQuery, Result<List<AgencyDto>>>
{
    public async Task<Result<List<AgencyDto>>> Handle(ListAgenciesQuery request, CancellationToken ct)
    {
        var query = db.Agencies.AsQueryable();

        if (!request.IncludeDeleted)
            query = query.Where(a => !a.IsDeleted);

        if (request.ParentAgencyId.HasValue)
            query = request.ParentAgencyId == Guid.Empty
                ? query.Where(a => a.ParentAgencyId == null)          // root-level only
                : query.Where(a => a.ParentAgencyId == request.ParentAgencyId);

        var agencies = await query
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

        return Result.Ok(agencies);
    }
}
