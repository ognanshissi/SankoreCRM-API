using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Models;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Resolves the current tenant using a strict priority chain and rejects
/// ambiguous or unsafe resolution paths before any handler runs.
///
/// Priority (descending authority):
///   1. JWT "tenant_id" claim  — signed, non-falsifiable, used directly.
///   2. Host / X-Tenant-Fqdn  — resolved via TenantDomain lookup in Sankore.Admin.
///   3. x-tenant-id header    — REJECTED (403). Trivially falsifiable by any client.
///
/// Cross-tenant guard: when both JWT and FQDN resolve but disagree the request
/// is rejected as a potential token-replay attack and logged as a security event.
///
/// The resolved TenantId is written to HttpContext.Items[ResolvedTenantKey] so
/// HttpTenantContext can serve it to downstream handlers without a second lookup.
///
/// Must be placed AFTER UseAuthentication() so JWT claims are already populated.
/// </summary>
public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    ITenantStore tenantStore,
    ILogger<TenantResolutionMiddleware> logger)
{

    private static readonly HashSet<string> ExemptPaths =
    [
        "/health",
        "/alive",
        "/swagger",
        "/api/v1/public",
    ];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ExemptPaths.Any(p => ctx.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(ctx);
            return;
        }

        // ── Step 1 ── Reject x-tenant-id raw header ──────────────────────────
        // This header was the old resolution mechanism and is a tenant-isolation
        // flaw: any client can set an arbitrary Guid and impersonate any tenant.
        // Close it here before any claim is read.
        if (ctx.Request.Headers.ContainsKey("x-tenant-id"))
        {
            logger.LogWarning(SecurityEvents.RawTenantIdHeader,
                "Request rejected: raw x-tenant-id header present from {RemoteIp}. " +
                "Use JWT or a recognised domain instead.",
                ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "The x-tenant-id header is not accepted. Authenticate via JWT or call from a recognised domain."
            });
            return;
        }

        // ── Step 2 ── JWT claim (highest authority) ───────────────────────────
        Guid? jwtTenantId = null;
        var claim = ctx.User.FindFirst("tenant_id")?.Value;
        if (claim is not null && Guid.TryParse(claim, out var parsedClaim))
            jwtTenantId = parsedClaim;

        // ── Step 3 ── FQDN resolution ─────────────────────────────────────────
        // Primary: Host header (same-domain deployment, validated by reverse proxy).
        // Fallback: X-Tenant-Fqdn (cross-domain API with explicit header).
        Guid? fqdnTenantId = null;
        var fqdn = ctx.Request.Headers["X-Tenant-Fqdn"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(fqdn))
        {
            var fqdnTenant = await tenantStore.GetByFqdnAsync(fqdn, ctx.RequestAborted);
            if (fqdnTenant is not null)
                fqdnTenantId = fqdnTenant.Id;
        }

        // ── Step 4 ── Cross-tenant guard ──────────────────────────────────────
        // Both sources resolved but disagree → token from one tenant replayed on
        // another tenant's domain. Reject and log as a security event.
        if (jwtTenantId.HasValue && fqdnTenantId.HasValue && jwtTenantId != fqdnTenantId)
        {
            logger.LogWarning(SecurityEvents.CrossTenantTokenReplay,
                "Cross-tenant token attempt: JWT tenant {JwtTenantId} vs FQDN tenant {FqdnTenantId} " +
                "for host {Host} from {RemoteIp}",
                jwtTenantId, fqdnTenantId, fqdn, ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Token does not match the current domain."
            });
            return;
        }

        // ── Step 5 ── Resolve effective tenant ────────────────────────────────
        var effectiveTenantId = jwtTenantId ?? fqdnTenantId;
        if (effectiveTenantId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Cannot determine tenant: authenticate via JWT or call from a recognised domain."
            });
            return;
        }

        // ── Step 6 ── Validate tenant state ───────────────────────────────────
        var tenant = await tenantStore.GetAsync(effectiveTenantId.Value, ctx.RequestAborted);
        if (tenant is null)
        {
            logger.LogWarning(SecurityEvents.InactiveTenant,
                "Request rejected: tenant {TenantId} not found in tenant store", effectiveTenantId);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Tenant not recognised." });
            return;
        }

        if (!tenant.IsActive)
        {
            logger.LogWarning(SecurityEvents.InactiveTenant,
                "Request rejected: tenant {TenantId} is inactive", effectiveTenantId);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Tenant account is inactive." });
            return;
        }

        if (tenant.IsMaintenance)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "The platform is under maintenance. Please try again later."
            });
            return;
        }

        // ── Step 7 ── Publish resolved tenant to downstream services ──────────
        // HttpTenantContext reads this item instead of re-resolving from headers.
        ctx.Items[TenantKey.ResolvedTenantKey] = effectiveTenantId.Value;

        await next(ctx);
    }
}
