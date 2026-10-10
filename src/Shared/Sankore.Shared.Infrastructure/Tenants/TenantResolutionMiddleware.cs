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
///
/// <para>
/// <b>Exempt paths.</b> This middleware runs before routing, so it answers for EVERY request —
/// including the public surfaces whose tenant comes from the URL rather than from a caller
/// identity: a lead-capture public key, a connection id, a relay agent's certificate. Those have
/// no JWT and no recognised domain by design, so without an exemption they get a flat
/// 400 "Cannot determine tenant" and the endpoint behind them is unreachable. The host passes its
/// own prefixes to <c>UseTenantResolution</c>, next to where it maps them, because they are the
/// host's routes and not this layer's to know.
/// </para>
/// </summary>
/// <summary>
/// The host's exempt prefixes, wrapped in a type of their own.
///
/// <para>
/// Not a bare <c>string[]</c> parameter: <c>UseMiddleware</c> takes
/// <c>params object?[] args</c>, and a <c>string[]</c> is covariant to <c>object?[]</c>, so the
/// compiler passes the ARRAY ITSELF as the argument list. Four prefixes became four separate
/// string arguments, matched nothing, and the host died at start-up with "A suitable constructor
/// for type 'TenantResolutionMiddleware' could not be located" — a failure the middleware's own
/// unit tests could not see, because they construct it directly and never go through
/// <c>UseMiddleware</c>. One non-array argument cannot be mistaken for the argument list.
/// </para>
/// </summary>
public sealed record TenantExemptPaths(string[] Prefixes);

public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    ITenantStore tenantStore,
    ILogger<TenantResolutionMiddleware> logger,
    TenantExemptPaths exemptPathPrefixes)
{
    /// <summary>
    /// Infrastructure paths that exist on every host. Everything else an application serves
    /// without a tenant is passed in by that application.
    /// </summary>
    private static readonly string[] AlwaysExempt =
    [
        "/health",
        "/alive",
        "/swagger",
        "/api/v1/public",
    ];

    private readonly string[] exemptPaths = [.. AlwaysExempt, .. exemptPathPrefixes.Prefixes];

    public async Task InvokeAsync(HttpContext ctx)
    {
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

        // ── Step 2 ── Public surfaces that carry their own tenant reference ──
        // AFTER the header rejection, and that order is the point: HttpTenantContext documents
        // "x-tenant-id is intentionally NOT read here, it is rejected upstream" and builds on
        // it. Exempting first made that claim false for exactly the routes an unauthenticated
        // caller can reach, and the next person to add a header fallback would have read a
        // comment that was no longer true.
        if (this.exemptPaths.Any(p => ctx.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(ctx);
            return;
        }

        // ── Step 3 ── JWT claim (highest authority) ───────────────────────────
        Guid? jwtTenantId = null;
        var claim = ctx.User.FindFirst("tenant_id")?.Value;
        if (claim is not null && Guid.TryParse(claim, out var parsedClaim))
            jwtTenantId = parsedClaim;

        // ── Step 4 ── FQDN resolution ─────────────────────────────────────────
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

        // ── Step 5 ── Cross-tenant guard ──────────────────────────────────────
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

        // ── Step 6 ── Resolve effective tenant ────────────────────────────────
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

        // ── Step 7 ── Validate tenant state ───────────────────────────────────
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

        // ── Step 8 ── Publish resolved tenant to downstream services ──────────
        // HttpTenantContext reads this item instead of re-resolving from headers.
        ctx.Items[TenantKey.ResolvedTenantKey] = effectiveTenantId.Value;

        await next(ctx);
    }
}
