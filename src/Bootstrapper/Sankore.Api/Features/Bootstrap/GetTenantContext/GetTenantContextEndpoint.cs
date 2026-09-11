using MediatR;

namespace Sankore.Api.Features.Bootstrap.GetTenantContext;

public static class GetTenantContextEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/tenant-context", async (HttpContext ctx, IMediator mediator, CancellationToken ct) =>
            {
                // Fallback: X-Tenant-Fqdn (cross-domain API setup).
                var fqdn = ctx.Request.Headers["X-Tenant-Fqdn"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(fqdn))
                    return Results.BadRequest(new { error = "Cannot determine tenant: no Host header or X-Tenant-Fqdn." });

                var result = await mediator.Send(new GetTenantContextQuery(fqdn), ct);

                // Unified 404 — do not distinguish "unknown FQDN" from "unknown tenant"
                // to prevent enumeration.
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetTenantContext")
            .WithSummary("Resolve public tenant context from the request Host")
            .WithDescription(
                "Unauthenticated. Called by the frontend on first load to get the TenantId " +
                "and initial UI state before login. Returns only public-safe fields.")
            .Produces<TenantContextResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .AllowAnonymous();
    }
}
