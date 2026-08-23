using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.ListAgencies;

internal sealed class ListAgenciesHandler(
    AdministrationDbContext db
) : IRequestHandler<ListAgenciesQuery, Result<PagedResult<AgencyDto>>>
{
    public async Task<Result<PagedResult<AgencyDto>>> Handle(ListAgenciesQuery request, CancellationToken ct)
    {
        var query = db.Agencies.AsQueryable();

        if (!request.IncludeDeleted)
            query = query.Where(a => !a.IsDeleted);

        if (request.ParentAgencyId.HasValue)
            query = request.ParentAgencyId == Guid.Empty
                ? query.Where(a => a.ParentAgencyId == null)
                : query.Where(a => a.ParentAgencyId == request.ParentAgencyId);

        query = query.OrderBy(a => a.Name);

        var totalCount = await query.CountAsync(ct);

        IQueryable<AgencyDto> projected = query.Select(a => new AgencyDto(
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
            a.UpdatedAt));

        List<AgencyDto> items;
        int page = request.Page;
        int pageSize = request.PageSize;

        if (pageSize > 0)
        {
            items = await projected
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);
        }
        else
        {
            items = await projected.ToListAsync(ct);
            page = 1;
            pageSize = totalCount == 0 ? 1 : totalCount;
        }

        return Result.Ok(new PagedResult<AgencyDto>(items, totalCount, page, pageSize));
    }
}
