using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.ListTenants;

public record ListTenantsQuery : IRequest<List<TenantResponse>>;

internal sealed class ListTenantsHandler(AdminDbContext db) : IRequestHandler<ListTenantsQuery, List<TenantResponse>>
{
    public async Task<List<TenantResponse>> Handle(ListTenantsQuery request, CancellationToken ct)
    {
        return await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TenantResponse(
                t.Id, t.Name, t.RootUserEmail, t.Fqdn,
                t.IsActive, t.IsMaintenance, t.TrialExpiresAt, t.BlockedAt,
                t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);
    }
}
