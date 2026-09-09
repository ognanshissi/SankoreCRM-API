using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Authentication.Login;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.RefreshToken;

internal sealed class RefreshTokenHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> jwtOptions) : IRequestHandler<RefreshTokenCommand, Result<LoginResult>>
{
    public async Task<Result<LoginResult>> Handle(RefreshTokenCommand request, CancellationToken ct)
    {
        // Bypass tenant filter — no JWT exists at refresh time.
        var existing = await db.RefreshTokens
            .AsTracking()
            .FirstOrDefaultAsync(r => r.Token == request.Token, ct);

        if (existing is null || !existing.IsActive)
            return Result.Fail<LoginResult>("Invalid or expired refresh token.");

        var user = await db.Users
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(u => u.Id == existing.UserId, ct);

        if (user is null || user.Status != UserStatus.Active)
            return Result.Fail<LoginResult>("Invalid or expired refresh token.");

        if (user.AccountType == UserAccountType.Service)
            return Result.Fail<LoginResult>("Invalid or expired refresh token.");

        // Rotate: revoke old token, issue new one.
        existing.Revoke();

        var newRefreshToken = Domain.RefreshToken.Create(
            user.TenantId, user.Id,
            TimeSpan.FromDays(jwtOptions.Value.RefreshTokenTtlDays));
        db.RefreshTokens.Add(newRefreshToken);

        await db.SaveChangesAsync(ct);

        var roles = await userManager.GetRolesAsync(user);

        var roleIds = await db.Roles
            .Where(r => roles.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var permissionCodes = await db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

        var scopedPermissionCodes = await db.PermissionAttributions
            .IgnoreQueryFilters()
            .Where(pa => pa.UserId == user.Id && pa.IsActive)
            .Select(pa => pa.PermissionCode)
            .Distinct()
            .ToListAsync(ct);

        var permissions = new HashSet<string>(permissionCodes);
        permissions.UnionWith(scopedPermissionCodes);

        var jwtResult = jwtTokenService.CreateToken(user, roles, permissions.ToArray());

        return Result.Ok(new LoginResult(
            jwtResult.Token,
            jwtResult.ExpiresAt,
            newRefreshToken.Token,
            newRefreshToken.ExpiresAt,
            user.Id));
    }
}
