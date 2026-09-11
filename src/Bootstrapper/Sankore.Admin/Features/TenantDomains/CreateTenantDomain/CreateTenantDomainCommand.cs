using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.TenantDomains.CreateTenantDomain;

public record CreateTenantDomainCommand(
    Guid TenantId,
    string Fqdn,
    bool IsPrimary,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo) : IRequest<Guid>;

internal sealed class CreateTenantDomainHandler(AdminDbContext db)
    : IRequestHandler<CreateTenantDomainCommand, Guid>
{
    public async Task<Guid> Handle(CreateTenantDomainCommand request, CancellationToken ct)
    {
        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == request.TenantId, ct);
        if (!tenantExists)
            throw new InvalidOperationException($"Tenant {request.TenantId} not found.");

        var fqdn = request.Fqdn.ToLowerInvariant();

        var fqdnTaken = await db.TenantDomains
            .AnyAsync(d => d.Fqdn == fqdn && d.IsActive, ct);
        if (fqdnTaken)
            throw new InvalidOperationException($"FQDN '{fqdn}' is already assigned to an active domain.");

        // Demote existing primary before assigning a new one.
        if (request.IsPrimary)
        {
            var currentPrimary = await db.TenantDomains
                .FirstOrDefaultAsync(d => d.TenantId == request.TenantId && d.IsPrimary && d.IsActive, ct);

            currentPrimary?.UnsetPrimary();
        }

        var domain = TenantDomain.Create(
            request.TenantId, fqdn, request.IsPrimary,
            request.ValidFrom, request.ValidTo);

        db.TenantDomains.Add(domain);
        await db.SaveChangesAsync(ct);

        return domain.Id;
    }
}
