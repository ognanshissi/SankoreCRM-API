using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Login;

internal sealed class LoginHandler(
    UserManager<AppUser> userManager,
    AdministrationDbContext db,
    IJwtTokenService jwtTokenService) : IRequestHandler<LoginCommand, Result<LoginResult>>
{
    public async Task<Result<LoginResult>> Handle(LoginCommand request, CancellationToken ct)
    {
        // Bypass the tenant query filter — no JWT exists yet at login time.
        // We scope to TenantId explicitly in the Where clause.
        var normalizedEmail = request.Email.ToUpperInvariant();
        var user = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == request.TenantId && u.NormalizedEmail == normalizedEmail)
            .FirstOrDefaultAsync(ct);

        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
            return Result.Fail<LoginResult>("Invalid credentials.");

        if (await userManager.IsLockedOutAsync(user))
            return Result.Fail<LoginResult>("Account is locked. Try again later.");

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

        var jwtTokenResult = jwtTokenService.CreateToken(user, roles, permissionCodes);

        return Result.Ok(new LoginResult(jwtTokenResult.Token, jwtTokenResult.ExpiresAt, user.Id, user.TenantId));
    }
}
