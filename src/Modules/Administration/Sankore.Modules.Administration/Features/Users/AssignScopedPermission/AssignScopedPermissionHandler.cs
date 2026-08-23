using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.AssignScopedPermission;

internal sealed class AssignScopedPermissionHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ICurrentUser currentUser
) : IRequestHandler<AssignScopedPermissionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AssignScopedPermissionCommand request, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.TenantId != currentUser.TenantId)
            return Result.Fail<Guid>("User not found.");

        // Guard: permission code must exist in the catalogue
        var permissionExists = await db.Permissions
            .AnyAsync(p => p.Code == request.PermissionCode, ct);
        if (!permissionExists)
            return Result.Fail<Guid>($"Permission code '{request.PermissionCode}' does not exist.");

        // Guard: no active duplicate (same user + code + scope)
        var duplicateExists = await db.PermissionAttributions
            .AnyAsync(a => a.UserId == request.UserId
                        && a.PermissionCode == request.PermissionCode
                        && a.ScopeId == request.ScopeId
                        && a.IsActive, ct);
        if (duplicateExists)
            return Result.Fail<Guid>("An active attribution with this permission and scope already exists.");

        var attribution = PermissionAttribution.Create(
            tenantId: currentUser.TenantId,
            userId: request.UserId,
            permissionCode: request.PermissionCode,
            assignedByUserId: currentUser.Id,
            startDate: request.StartDate,
            endDate: request.EndDate,
            scopeId: request.ScopeId,
            scopeType: request.ScopeType);

        await db.PermissionAttributions.AddAsync(attribution, ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok(attribution.Id);
    }
}
