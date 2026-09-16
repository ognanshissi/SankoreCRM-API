using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.GetUserRoles;

internal sealed class GetUserRolesHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<GetUserRolesQuery, Result<List<UserRoleDto>>>
{
    public async Task<Result<List<UserRoleDto>>> Handle(
        GetUserRolesQuery request, CancellationToken ct)
    {
        var userExists = await db.Users
            .AnyAsync(u => u.Id == request.UserId && u.TenantId == currentUser.TenantId, ct);

        if (!userExists)
            return Result.Fail<List<UserRoleDto>>("User not found.");

        var roles = await db.UserRoles
            .Where(ur => ur.UserId == request.UserId && ur.IsActive)
            .Join(db.Roles,
                ur => ur.RoleId,
                r => r.Id,
                (ur, r) => new UserRoleDto(r.Id, r.Name!, r.Label, r.IsSystem, ur.AssignedAt))
            .ToListAsync(ct);

        return Result.Ok(roles);
    }
}
