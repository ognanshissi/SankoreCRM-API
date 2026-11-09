using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Features.TenantDomains;
using Sankore.Admin.Infrastructure;
using Sankore.Shared.Infrastructure.Tenants;

namespace Sankore.Admin.Features.Tenants.UpdateTenant;

public record UpdateTenantCommand(
    Guid Id,
    string Name,
    string Fqdn,
    bool IsActive,
    bool IsMaintenance) : IRequest<bool>;


/// <summary>
/// A new main origin moves the tenant's primary domain row with it (<see cref="TenantPrimaryDomain"/>);
/// the old one stays as a secondary domain, so links already sent keep opening the tenant. An origin
/// held by another tenant throws <see cref="InvalidOperationException"/> (409 at the endpoint) instead
/// of reaching the unique indexes as a 500.
/// </summary>
internal sealed class UpdateTenantHandler(AdminDbContext db) : IRequestHandler<UpdateTenantCommand, bool>
{
    public async Task<bool> Handle(UpdateTenantCommand request, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == request.Id, ct);
        if (tenant is null) return false;

        var fqdn = TenantFqdn.Normalize(request.Fqdn);
        if (!string.Equals(tenant.Fqdn, fqdn, StringComparison.OrdinalIgnoreCase))
        {
            if (await db.Tenants.AnyAsync(t => t.Fqdn == fqdn && t.Id != tenant.Id, ct)
                || await TenantPrimaryDomain.TakenByAnotherTenantAsync(db, tenant.Id, fqdn, ct))
                throw new InvalidOperationException($"A tenant with FQDN '{fqdn}' already exists.");
        }

        tenant.Update(request.Name, fqdn);
        await TenantPrimaryDomain.SyncAsync(db, tenant.Id, fqdn, ct);

        if (request.IsActive) tenant.Activate();
        else tenant.Deactivate();

        tenant.SetMaintenance(request.IsMaintenance);

        await db.SaveChangesAsync(ct);
        return true;
    }
}
