namespace Sankore.Modules.Leads.Features.Ingestion.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Public endpoint for embedded script installation detection (F13.37-BE-13).
/// No JWT required — authentication is via source PublicKey.
/// </summary>
public static class WebPingEndpoint
{
    public static IEndpointRouteBuilder MapWebPingEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/web/{publicKey}/ping", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting("ingest-key")
            .WithName("WebPing")
            .WithTags("Ingest")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string publicKey,
        HttpContext http,
        LeadsDbContext db,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("WebPing");

        // Resolve source by PublicKey (no tenant context — public endpoint)
        var source = await db.LeadSourceConfigs
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PublicKey == publicKey
                                   && s.Mode == IntegrationMode.EmbeddedScript, ct);

        if (source is null)
            return Results.NotFound();

        // Extract origin from request
        var origin = http.Request.Headers.Origin.FirstOrDefault()
                  ?? http.Request.Headers.Referer.FirstOrDefault()
                  ?? "unknown";

        // Check if origin is allowed
        var settings = source.Settings as EmbeddedScriptSettings;
        var allowedOrigins = settings?.AllowedOrigins ?? [];

        var isAllowed = allowedOrigins.Count == 0
            || allowedOrigins.Any(ao =>
                origin.Contains(ao, StringComparison.OrdinalIgnoreCase));

        if (!isAllowed)
        {
            source.RecordUnauthorizedOrigin(origin);
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Unauthorized ping origin {Origin} for source {SourceId}",
                origin, source.Id);

            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Record authorized ping (throttled to 1/min)
        var now = clock.GetUtcNow();
        var wasUpdated = source.RecordPing(origin, now);

        if (wasUpdated)
        {
            // If source transitioned Draft → Testing, create an InstallCheck run
            if (source.Status == LeadSourceStatus.Testing)
            {
                var run = LeadSourceRun.Start(source.TenantId, source.Id, clock);
                run.Complete(0, 0, 0, 0, clock);
                db.LeadSourceRuns.Add(run);

                logger.LogInformation(
                    "Source {SourceId} transitioned to Testing after first ping from {Origin}",
                    source.Id, origin);
            }

            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }
}
