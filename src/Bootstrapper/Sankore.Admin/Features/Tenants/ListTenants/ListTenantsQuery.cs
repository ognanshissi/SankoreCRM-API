using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.ListTenants;

/// <param name="IncludeTwins">
/// Test twins are listed only when asked: the platform's tenant list shows institutions, while the
/// API needs every tenant it runs jobs and seeders for.
/// </param>
public record ListTenantsQuery(bool? ActiveOnly, bool IncludeTwins = false) : IRequest<List<TenantResponse>>;

internal sealed class ListTenantsHandler(AdminDbContext db) : IRequestHandler<ListTenantsQuery, List<TenantResponse>>
{
    public async Task<List<TenantResponse>> Handle(ListTenantsQuery request, CancellationToken ct)
    {
        var query = db.Tenants.AsQueryable();

        if (!request.IncludeTwins)
            query = query.Where(t => t.ParentTenantId == null);

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
