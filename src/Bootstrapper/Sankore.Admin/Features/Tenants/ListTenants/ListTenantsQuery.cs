using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.ListTenants;

public record ListTenantsQuery(bool? ActiveOnly) : IRequest<List<TenantResponse>>;

internal sealed class ListTenantsHandler(AdminDbContext db) : IRequestHandler<ListTenantsQuery, List<TenantResponse>>
{
    public async Task<List<TenantResponse>> Handle(ListTenantsQuery request, CancellationToken ct)
    {
        var query = db.Tenants.AsQueryable();

        if (request.ActiveOnly.HasValue)
        {
            query = query.Where(a => a.IsActive == request.ActiveOnly.Value);
        }
        
        return await query
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .SelectResponse(db)
            .ToListAsync(ct);
    }
}
