namespace Sankore.Modules.Leads.Features.Ingestion.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Handles CORS preflight (OPTIONS) for the web ingest endpoint.
/// Dynamically resolves AllowedOrigins from the source's EmbeddedScriptSettings.
/// </summary>
public static class WebIngestCorsEndpoint
{
    public static IEndpointRouteBuilder MapWebIngestCorsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/api/ingest/web/{publicKey}", ["OPTIONS"], HandlePreflightAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static async Task<IResult> HandlePreflightAsync(
        string publicKey,
        HttpContext httpContext,
        LeadsDbContext db,
        CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PublicKey == publicKey, ct);

        if (source is null)
            return Results.NotFound();

        var settings = source.Settings as EmbeddedScriptSettings;
        var origin = httpContext.Request.Headers.Origin.ToString();

        if (settings?.AllowedOrigins.Count > 0 && !string.IsNullOrEmpty(origin))
        {
            var originUri = new Uri(origin);
            var allowed = settings.AllowedOrigins.Any(ao =>
            {
                if (!Uri.TryCreate(ao, UriKind.Absolute, out var allowedUri)) return false;
                return string.Equals(originUri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase)
                       && originUri.Port == allowedUri.Port
                       && string.Equals(originUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase);
            });

            if (!allowed)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            httpContext.Response.Headers.Append("Access-Control-Allow-Origin", origin);
            httpContext.Response.Headers.Append("Access-Control-Allow-Methods", "POST, OPTIONS");
            httpContext.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type");
            httpContext.Response.Headers.Append("Access-Control-Max-Age", "86400");
        }

        return Results.NoContent();
    }
}
