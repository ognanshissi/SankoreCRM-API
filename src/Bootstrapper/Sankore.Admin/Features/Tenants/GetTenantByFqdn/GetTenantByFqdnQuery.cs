using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.GetTenantByFqdn;

public record GetTenantByFqdnQuery(string Fqdn) : IRequest<TenantResponse?>;

internal sealed class GetTenantByFqdnHandler(AdminDbContext db)
    : IRequestHandler<GetTenantByFqdnQuery, TenantResponse?>
{
    public async Task<TenantResponse?> Handle(GetTenantByFqdnQuery request, CancellationToken ct)
    {
        return await db.Tenants
            .AsNoTracking()
            .Where(t => t.Fqdn == request.Fqdn)
            .Select(t => new TenantResponse(
                t.Id, t.Name, t.RootUserEmail, t.Fqdn,
                t.IsActive, t.IsMaintenance, t.TrialExpiresAt, t.BlockedAt,
                t.CreatedAt, t.UpdatedAt))
            .FirstOrDefaultAsync(ct);
    }
}