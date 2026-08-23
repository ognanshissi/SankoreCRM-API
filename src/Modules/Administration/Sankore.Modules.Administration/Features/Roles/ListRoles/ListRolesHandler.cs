using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Roles.ListRoles;

internal sealed class ListRolesHandler(AdministrationDbContext db)
    : IRequestHandler<ListRolesQuery, Result<List<RoleDto>>>
{
    public async Task<Result<List<RoleDto>>> Handle(ListRolesQuery request, CancellationToken ct)
    {
        var roles = await db.Roles
            .AsNoTracking()
            .Where(r => r.IsAssignable)
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto(r.Id, r.Name!, r.Label, r.IsSystem, r.IsAssignable))
            .ToListAsync(ct);

        return Result.Ok(roles);
    }
}
