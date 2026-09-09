using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.DeleteTenant;

public record DeleteTenantCommand(Guid Id) : IRequest<bool>;

internal sealed class DeleteTenantHandler(AdminDbContext db) : IRequestHandler<DeleteTenantCommand, bool>
{
    public async Task<bool> Handle(DeleteTenantCommand request, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == request.Id, ct);
        if (tenant is null) return false;

        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(ct);
        return true;
    }
}