using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.TenantDomains.DeactivateTenantDomain;

public record DeactivateTenantDomainCommand(Guid Id) : IRequest<DeactivateTenantDomainResponse>;

/// <param name="Refusal">Set when the domain exists but may not be deactivated.</param>
public record DeactivateTenantDomainResponse(bool Success, string? Refusal = null);

internal sealed class DeactivateTenantDomainHandler(AdminDbContext db)
    : IRequestHandler<DeactivateTenantDomainCommand, DeactivateTenantDomainResponse>
{
    public async Task<DeactivateTenantDomainResponse> Handle(DeactivateTenantDomainCommand request, CancellationToken ct)
    {
        var domain = await db.TenantDomains
            .FirstOrDefaultAsync(d => d.Id == request.Id, ct);

        if (domain is null) return new DeactivateTenantDomainResponse(false);

        // The primary domain mirrors tenants.fqdn, which is what resolution reads first: turning
        // the row off would leave the origin resolving exactly as before while the registry says
        // it is gone. Cutting a tenant's main access is deactivating the TENANT.
        var isMainOrigin = await db.Tenants.AnyAsync(t => t.Id == domain.TenantId && t.Fqdn == domain.Fqdn, ct);
        if (domain.IsPrimary || isMainOrigin)
            return new DeactivateTenantDomainResponse(false,
                "Le domaine principal ne se désactive pas : désactivez le tenant pour couper son accès.");

        if (domain.IsActive)
        {
            domain.Deactivate();
            await db.SaveChangesAsync(ct);
        }

        return new DeactivateTenantDomainResponse(true);
    }
}
