using Sankore.Admin.Features.Auth.Login;
using Sankore.Admin.Features.Auth.Logout;
using Sankore.Admin.Features.Auth.Me;
using Sankore.Admin.Features.Auth.RefreshToken;

namespace Sankore.Admin.Features.Auth;

public static class AuthEndpoints
{
    /// <summary>
    /// Excluded from <c>UseApiKeyAuth</c> in Program.cs: these routes are what a browser calls,
    /// and the shared service key must never reach one.
    /// </summary>
    public const string Prefix = "/api/v1/auth";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(Prefix)
            .WithTags("Auth")
            .RequireCors(Infrastructure.Auth.AdminAuthSetup.CorsPolicy);

        LoginEndpoint.Map(group);
        RefreshTokenEndpoint.Map(group);
        LogoutEndpoint.Map(group);
        GetCurrentAdminEndpoint.Map(group);
    }
}
