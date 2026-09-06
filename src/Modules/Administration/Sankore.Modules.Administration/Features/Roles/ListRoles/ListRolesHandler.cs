using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.ListRoles;

internal sealed class ListRolesHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<ListRolesQuery, Result<List<RoleDto>>>
{
    public async Task<Result<List<RoleDto>>> Handle(ListRolesQuery request, CancellationToken ct)
    {
        var roles = await db.Roles
            .AsNoTracking()
            // System roles (TenantId == null) are visible to all tenants.
            // Custom roles are scoped to the calling tenant.
            .Where(r => r.IsAssignable
                     && (r.TenantId == null || r.TenantId == currentUser.TenantId))
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(r.Id, r.Name!, r.Label, r.IsSystem, r.IsAssignable))
            .ToListAsync(ct);

        return Result.Ok(roles);
    }
}
