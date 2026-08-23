using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.RevokeScopedPermission;

internal sealed class RevokeScopedPermissionHandler(
    AdministrationDbContext db,
    ICurrentUser currentUser
) : IRequestHandler<RevokeScopedPermissionCommand, Result>
{
    public async Task<Result> Handle(RevokeScopedPermissionCommand request, CancellationToken ct)
    {
        var attribution = await db.PermissionAttributions
            .FirstOrDefaultAsync(a => a.Id == request.AttributionId
                                   && a.UserId == request.UserId, ct);

        if (attribution is null)
            return Result.Fail("Permission attribution not found.");

        // Tenant guard (PermissionAttributions has global query filter, but explicit check for clarity)
        if (attribution.TenantId != currentUser.TenantId)
            return Result.Fail("Permission attribution not found.");

        if (!attribution.IsActive)
            return Result.Fail("Permission attribution is already revoked.");

        attribution.Revoke();
        db.PermissionAttributions.Update(attribution);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}
