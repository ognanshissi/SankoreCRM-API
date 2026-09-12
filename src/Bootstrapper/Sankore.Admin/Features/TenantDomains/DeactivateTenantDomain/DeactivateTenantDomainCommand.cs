using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.TenantDomains.DeactivateTenantDomain;

public record DeactivateTenantDomainCommand(Guid Id) : IRequest<DeactivateTenantDomainResponse>;

public record DeactivateTenantDomainResponse(bool Success);

internal sealed class DeactivateTenantDomainHandler(AdminDbContext db)
    : IRequestHandler<DeactivateTenantDomainCommand, DeactivateTenantDomainResponse>
{
    public async Task<DeactivateTenantDomainResponse> Handle(DeactivateTenantDomainCommand request, CancellationToken ct)
    {
        var domain = await db.TenantDomains
            .FirstOrDefaultAsync(d => d.Id == request.Id, ct);

        if (domain is null) return new DeactivateTenantDomainResponse(false);

        domain.Deactivate();
        await db.SaveChangesAsync(ct);

        return new DeactivateTenantDomainResponse(true);
    }
}
