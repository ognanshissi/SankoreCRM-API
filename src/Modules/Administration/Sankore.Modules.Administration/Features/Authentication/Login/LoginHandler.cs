using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Features.Authentication.Login;

internal sealed class LoginHandler(
    UserManager<AppUser> userManager,
    AdministrationDbContext db,
    ITenantContext tenant,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> jwtOptions) : IRequestHandler<LoginCommand, Result<LoginResult>>
{
    public async Task<Result<LoginResult>> Handle(LoginCommand request, CancellationToken ct)
    {
        // Bypass the tenant query filter — no JWT exists yet at login time.
        // We scope to TenantId explicitly in the Where clause.
        var normalizedEmail = request.Email.ToUpperInvariant();
        var user = await db.Users
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(u => u.TenantId == tenant.CurrentTenantId && u.NormalizedEmail == normalizedEmail)
            .FirstOrDefaultAsync(ct);

        if (user is null)
        {
            return Result.Fail<LoginResult>("Invalid login attempt.");
        }
        
        // Only activated user can login
        if (user.Status != UserStatus.Active)
        {
            user.IncrementFailedLogin();
            db.Users.Update(user);
            await db.SaveChangesAsync(ct);
            return Result.Fail<LoginResult>("Invalid login attempt.");
        }
        
        // Only UserAccountType (Standard / System) can authenticate
        if (user.AccountType == UserAccountType.Service) 
            return Result.Fail<LoginResult>("Invalid login attempt.");

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            user.IncrementFailedLogin();
            db.Users.Update(user);
            await db.SaveChangesAsync(ct);
            return Result.Fail<LoginResult>("Invalid login attempt.");
        }
        
        if (await userManager.IsLockedOutAsync(user))
            return Result.Fail<LoginResult>("Account is locked. Try again later.");

        user.RecordSuccessfulLogin();
        db.Users.Update(user);

        // LoginHistory
        var userLogin = UserLoginLocation.Create(tenant.CurrentTenantId, user.Id, new GeoPoint(0, 0));
        db.UserLoginLocations.Add(userLogin);

        var refreshToken = Domain.RefreshToken.Create(
            user.TenantId, user.Id,
            TimeSpan.FromDays(jwtOptions.Value.RefreshTokenTtlDays));
        db.RefreshTokens.Add(refreshToken);

        await db.SaveChangesAsync(ct);
        
        var roles = await userManager.GetRolesAsync(user);

        // Load permission codes assigned to this user's roles
        var roleIds = await db.Roles
            .Where(r => roles.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var permissionCodes = await db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

        // valid attributed permissions
        var scopedPermissionCodes =
            await db.PermissionAttributions
                .Where(pa => pa.UserId == user.Id && pa.IsActive)
                .Select(pa => pa.PermissionCode)
                .Distinct()
                .ToListAsync(ct);

        HashSet<string> permissions = new HashSet<string>();
        permissions.UnionWith(permissionCodes);
        permissions.UnionWith(scopedPermissionCodes);
        
        var jwtTokenResult = jwtTokenService.CreateToken(user, roles, permissions.ToArray());

        return Result.Ok(new LoginResult(
            jwtTokenResult.Token,
            jwtTokenResult.ExpiresAt,
            refreshToken.Token,
            refreshToken.ExpiresAt,
            user.Id));
    }
}
