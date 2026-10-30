using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Authentication.Login;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.TestMode;

public static class TestModeErrors
{
    /// <summary>Entering test mode needs <c>tenant:test-mode</c>; leaving it never does.</summary>
    public const string Forbidden = "TEST_MODE_FORBIDDEN";
    public const string Impersonating = "TEST_MODE_IMPERSONATING";
    /// <summary>The registry could not create or confirm the twin.</summary>
    public const string Unavailable = "TEST_MODE_UNAVAILABLE";
    public const string SessionInvalid = "TEST_MODE_SESSION_INVALID";
}

/// <summary>
/// Switches the caller's session into or out of « Mode test » (sandbox lot 2), Stripe-style: same
/// account, same session, a new pair of tokens whose <c>tenant_id</c> names the tenant's TEST TWIN
/// — its own data and configuration — or the tenant again. The refresh token presented is revoked
/// and replaced by one remembering the mode, so a refresh keeps it.
/// </summary>
public sealed record SwitchTestModeCommand(bool Test, [property: SensitiveData] string RefreshToken)
    : IRequest<Result<LoginResult>>, ICommand;

public sealed record SwitchTestModeRequest(bool Test, string RefreshToken);

internal sealed class SwitchTestModeHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ICurrentUser me,
    ITenantStore tenantStore,
    ITenantCacheInvalidator tenantCache,
    ITestTwinRegistry registry,
    TenantProvisioner provisioner,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> jwtOptions,
    ILogger<SwitchTestModeHandler> logger) : IRequestHandler<SwitchTestModeCommand, Result<LoginResult>>
{
    public async Task<Result<LoginResult>> Handle(SwitchTestModeCommand cmd, CancellationToken ct)
    {
        // An administrator acting as somebody else stays where that person is.
        if (me.ImpersonatorId is not null) return Result.Fail<LoginResult>(TestModeErrors.Impersonating);
        if (cmd.Test && !me.Permissions.Contains(Permissions.CanUseTestMode.Code))
            return Result.Fail<LoginResult>(TestModeErrors.Forbidden);

        var team = me.TeamTenantId;
        var user = await db.Users.IgnoreQueryFilters().AsTracking()
            .FirstOrDefaultAsync(u => u.Id == me.Id && u.TenantId == team && u.Status == UserStatus.Active, ct);
        var existing = user is null ? null : await db.RefreshTokens.IgnoreQueryFilters().AsTracking()
            .FirstOrDefaultAsync(r => r.Token == cmd.RefreshToken && r.UserId == user.Id && r.TenantId == team, ct);
        if (user is null || existing is null || !existing.IsActive)
            return Result.Fail<LoginResult>(TestModeErrors.SessionInvalid);

        Guid? twin = null;
        if (cmd.Test)
        {
            var ensured = await EnsureTwinAsync(team, ct);
            if (ensured is null) return Result.Fail<LoginResult>(TestModeErrors.Unavailable);
            twin = ensured;
        }

        existing.Revoke();
        var next = Domain.RefreshToken.Create(
            team, user.Id, TimeSpan.FromDays(jwtOptions.Value.RefreshTokenTtlDays), existing.SessionId, twin);
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);

        var roles = await userManager.GetRolesAsync(user);
        var permissions = await AccessClaims.PermissionsAsync(db, user.Id, roles, ct);
        var tenant = await tenantStore.GetAsync(team, ct);
        var language = user.PreferredLanguage ?? tenant?.DefaultLanguage ?? "fr";

        var access = jwtTokenService.CreateToken(user, roles, permissions, language, existing.SessionId, dataTenantId: twin);
        logger.LogInformation("User {UserId} switched {Mode} on tenant {TenantId}", user.Id, cmd.Test ? "to test mode" : "to production", team);

        return Result.Ok(new LoginResult(access.Token, access.ExpiresAt, next.Token, next.ExpiresAt, user.Id));
    }

    /// <summary>
    /// The twin of <paramref name="team"/>, created on the first switch. A new twin is seeded like
    /// a tenant at start-up — settings, default rule, task types, KYC circuit — before anybody
    /// lands in it; its configuration is otherwise empty until copied (lot 5) or built.
    /// </summary>
    private async Task<Guid?> EnsureTwinAsync(Guid team, CancellationToken ct)
    {
        var known = (await tenantStore.GetAsync(team, ct))?.TestTenantId;

        Guid twin;
        if (known is { } id) twin = id;
        else
        {
            var created = await registry.EnsureTwinAsync(team, ct);
            if (!created.IsSuccess) return null;
            twin = created.Value;
            await tenantCache.InvalidateAsync(team, ct);
        }

        // Read through the store: remembers the twin for this process and checks it is ours.
        var info = await tenantStore.GetAsync(twin, ct);
        if (info?.ParentTenantId != team || !info.IsActive) return null;

        if (known is null) await provisioner.SeedAllAsync(ct);
        return twin;
    }
}

/// <summary>Role and scoped permissions of an account, as login and refresh put them in a token.</summary>
internal static class AccessClaims
{
    public static async Task<string[]> PermissionsAsync(
        AdministrationDbContext db, Guid userId, IList<string> roles, CancellationToken ct)
    {
        var roleIds = await db.Roles.Where(r => roles.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);
        var fromRoles = await db.RolePermissions.Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission.Code).Distinct().ToListAsync(ct);
        var scoped = await db.PermissionAttributions.IgnoreQueryFilters()
            .Where(pa => pa.UserId == userId && pa.IsActive).Select(pa => pa.PermissionCode).Distinct().ToListAsync(ct);
        return [.. fromRoles.Union(scoped)];
    }
}

public static class SwitchTestModeEndpoint
{
    public static IEndpointRouteBuilder MapSwitchTestMode(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/test-mode", async (SwitchTestModeRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new SwitchTestModeCommand(req.Test, req.RefreshToken), ct);
                if (result.IsSuccess) return Results.Ok(result.Value);

                return Results.Problem(detail: result.Error, statusCode: result.Error switch
                {
                    TestModeErrors.Forbidden or TestModeErrors.Impersonating => StatusCodes.Status403Forbidden,
                    TestModeErrors.Unavailable => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status401Unauthorized,
                });
            })
            .WithTags("Auth")
            .WithName("SwitchTestMode")
            .WithSummary("Switch the session into or out of « Mode test »")
            .WithDescription(
                "Answers a new token pair whose tenant is the tenant's test twin (test: true) — its own "
                + "data and configuration, production's users — or production again. The refresh token "
                + "presented is revoked. Entering needs tenant:test-mode and creates the twin on first "
                + "use; leaving needs nothing. 403 TEST_MODE_FORBIDDEN / TEST_MODE_IMPERSONATING, 401 "
                + "TEST_MODE_SESSION_INVALID, 503 TEST_MODE_UNAVAILABLE.")
            .RequireAuthorization()
            .Produces<LoginResult>(StatusCodes.Status200OK)
            .WithOpenApi();
        return app;
    }
}
