using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Features.Tenants.GetTenant;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;

namespace Sankore.Admin.Features.Tenants.GetTenantByFqdn;

public record GetTenantByFqdnQuery(string Fqdn) : IRequest<TenantResponse?>;

/// <summary>
/// Resolves the tenant an origin belongs to: its main <c>tenants.fqdn</c> first, then any
/// EFFECTIVE secondary domain in <c>tenant_domains</c> (active and inside its validity window).
///
/// <para>
/// It used to read <c>tenants.fqdn</c> alone, so a domain added to a tenant was recorded and then
/// never matched — the institution's second front-end answered 400 "Cannot determine tenant" with
/// the domain plainly listed in the registry. The admin front-end now adds domains, so the lookup
/// has to honour them.
/// </para>
/// </summary>
internal sealed class GetTenantByFqdnHandler(AdminDbContext db)
    : IRequestHandler<GetTenantByFqdnQuery, TenantResponse?>
{
    public async Task<TenantResponse?> Handle(GetTenantByFqdnQuery request, CancellationToken ct)
    {
        var fqdn = TenantFqdn.Normalize(request.Fqdn);

        var tenantId = await db.Tenants.AsNoTracking()
            .Where(t => t.Fqdn == fqdn)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (tenantId is null)
        {
            var now = DateTimeOffset.UtcNow;
            tenantId = await db.TenantDomains.AsNoTracking()
                .Where(d => d.Fqdn == fqdn && d.IsActive && d.ValidFrom <= now && (d.ValidTo == null || d.ValidTo > now))
                .Select(d => (Guid?)d.TenantId)
                .FirstOrDefaultAsync(ct);
        }

        if (tenantId is null)
            return null;

        return await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantResponse(
                t.Id, t.Name, t.RootUserEmail, t.Fqdn,
                t.IsActive, t.IsMaintenance, t.TrialExpiresAt, t.BlockedAt,
                t.CreatedAt, t.UpdatedAt))
            .FirstOrDefaultAsync(ct);
    }
}
