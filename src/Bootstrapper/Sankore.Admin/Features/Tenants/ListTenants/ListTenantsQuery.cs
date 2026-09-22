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
            .Select(t => new TenantResponse(
                t.Id, t.Name, t.RootUserEmail, t.Fqdn,
                t.IsActive, t.IsMaintenance, t.TrialExpiresAt, t.BlockedAt,
                t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);
    }
}
