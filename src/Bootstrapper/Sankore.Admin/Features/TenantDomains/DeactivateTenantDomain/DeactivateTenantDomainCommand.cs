using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.TenantDomains.DeactivateTenantDomain;

public record DeactivateTenantDomainCommand(Guid Id) : IRequest<bool>;

internal sealed class DeactivateTenantDomainHandler(AdminDbContext db)
    : IRequestHandler<DeactivateTenantDomainCommand, bool>
{
    public async Task<bool> Handle(DeactivateTenantDomainCommand request, CancellationToken ct)
    {
        var domain = await db.TenantDomains
            .FirstOrDefaultAsync(d => d.Id == request.Id, ct);

        if (domain is null) return false;

        domain.Deactivate();
        await db.SaveChangesAsync(ct);

        return true;
    }
}
