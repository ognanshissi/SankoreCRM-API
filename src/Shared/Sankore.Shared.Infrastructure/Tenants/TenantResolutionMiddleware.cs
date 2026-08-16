using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Middleware that:
///   1. Extracts the tenant from the JWT "tenant_id" claim or the "x-tenant-id" header.
///   2. Verifies the tenant exists in the external tenant store.
///   3. Short-circuits with 400 (missing) or 401 (unknown tenant) before any handler runs.
///
/// Must be placed AFTER UseAuthentication() so JWT claims are already populated.
/// </summary>
public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    ITenantStore tenantStore,
    ILogger<TenantResolutionMiddleware> logger)
{
    // Paths that carry no tenant context (health probes, swagger assets, etc.)
    private static readonly HashSet<string> ExemptPaths =
    [
        "/health",
        "/swagger",
    ];

    public async Task InvokeAsync(HttpContext ctx, ITenantContext tenantContext)
    {
        if (ExemptPaths.Any(p => ctx.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(ctx);
            return;
        }

        if (!tenantContext.HasTenant)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Missing tenant. Provide a JWT with a tenant_id claim or an x-tenant-id header."
            });
            return;
        }

        var tenantId = tenantContext.CurrentTenantId;
        var exists = await tenantStore.ExistsAsync(tenantId, ctx.RequestAborted);

        if (!exists)
        {
            logger.LogWarning("Request rejected: tenant {TenantId} not found in tenant store", tenantId);

            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = $"Tenant {tenantId} is not recognized."
            });
            return;
        }

        await next(ctx);
    }
}
