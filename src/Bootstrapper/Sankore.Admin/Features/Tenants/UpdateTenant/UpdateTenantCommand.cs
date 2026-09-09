using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Infrastructure;

namespace Sankore.Admin.Features.Tenants.UpdateTenant;

public record UpdateTenantCommand(
    Guid Id,
    string Name,
    string Fqdn,
    bool IsActive,
    bool IsMaintenance) : IRequest<bool>;

internal sealed class UpdateTenantHandler(AdminDbContext db) : IRequestHandler<UpdateTenantCommand, bool>
{
    public async Task<bool> Handle(UpdateTenantCommand request, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == request.Id, ct);
        if (tenant is null) return false;

        tenant.Update(request.Name, request.Fqdn);

        if (request.IsActive) tenant.Activate();
        else tenant.Deactivate();

        tenant.SetMaintenance(request.IsMaintenance);

        await db.SaveChangesAsync(ct);
        return true;
    }
}
