namespace Sankore.Modules.Administration.Features.Users.GetCurrentUser;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;

public static class GetCurrentUserEndpoint
{
    public static IEndpointRouteBuilder MapGetCurrentUser(this IEndpointRouteBuilder app)
    {
        app.MapGet("users/auth-context", Handle)
            .WithName("GetCurrentUser")
            .WithTags("Users")
            .WithSummary("Returns the connected user's profile, roles and permissions")
            .RequireAuthorization()
            .Produces<CurrentUserDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ICurrentUser currentUser,
        UserManager<AppUser> userManager,
        AdministrationDbContext db,
        CancellationToken ct)
    {
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);

        if (user is null)
            return Results.Unauthorized();

        var roles = await userManager.GetRolesAsync(user);

        var roleIds = await db.Roles
            .Where(r => roles.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        // Role-based permissions
        var rolePermissions = await db.RolePermissions
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync(ct);

        // Scoped permissions (PermissionAttribution)
        var scopedPermissions = await db.PermissionAttributions
            .Where(pa => pa.UserId == user.Id && pa.IsActive)
            .Select(pa => pa.PermissionCode)
            .Distinct()
            .ToListAsync(ct);

        var allPermissions = rolePermissions
            .Union(scopedPermissions)
            .OrderBy(p => p)
            .ToList();

        var profile = await db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == user.Id, ct);

        return Results.Ok(new CurrentUserDto(
            Id:              user.Id,
            TenantId:        user.TenantId,
            Email:           user.Email!,
            FullName:        user.FullName,
            AgencyId:        user.AgencyId,
            IsSuperUser:     user.IsSuperUser,
            Status:          user.Status.ToString(),
            AccountType:     user.AccountType.ToString(),
            Roles:           roles.ToList(),
            Permissions:     allPermissions,
            DefaultLanguage: profile?.DefaultLanguage ?? "fr"));
    }
}

public sealed record CurrentUserDto(
    Guid Id,
    Guid TenantId,
    string Email,
    string FullName,
    Guid? AgencyId,
    bool IsSuperUser,
    string Status,
    string AccountType,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string DefaultLanguage);
