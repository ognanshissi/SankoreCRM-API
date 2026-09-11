using Sankore.Api.Features.Bootstrap.GetTenantContext;

namespace Sankore.Api.Features.Bootstrap;

public static class BootstrapEndpoints
{
    public static void MapBootstrapEndpoints(this WebApplication app)
    {
        // /api/v1/public/* — no authentication, no tenant resolution.
        // TenantResolutionMiddleware exempts this prefix (see ExemptPaths).
        app.MapGroup("/api/v1/public")
            .WithTags("Bootstrap")
            .RequireRateLimiting("auth")
            .AllowAnonymous()
            .MapBootstrapRoutes();
    }

    private static RouteGroupBuilder MapBootstrapRoutes(this RouteGroupBuilder group)
    {
        GetTenantContextEndpoint.Map(group);
        return group;
    }
}