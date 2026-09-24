using Microsoft.Extensions.DependencyInjection;

namespace Sankore.Modules.Leads.Features.Ingestion.Web;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Public endpoint serving the hosted form definition for the embedded SDK (F13.37-BE-16).
/// No JWT — authenticated by PublicKey. Origin-checked. Cached 5 min with ETag.
/// Returns only public data: fields, labels, consent, theme, captcha — no internal data.
/// </summary>
public static class WebFormEndpoint
{
    public static IEndpointRouteBuilder MapWebFormEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ingest/web/{publicKey}/form", HandleAsync)
            .AllowAnonymous()
            .WithName("WebForm")
            .WithTags("Ingest")
            .Produces<WebFormResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status304NotModified)
            .CacheOutput(p => p.Expire(TimeSpan.FromMinutes(5)).SetVaryByRouteValue("publicKey"))
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string publicKey,
        HttpContext http,
        LeadsDbContext db,
        CancellationToken ct)
    {
        var source = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PublicKey == publicKey
                                   && s.Mode == IntegrationMode.EmbeddedScript, ct);

        if (source is null)
            return Results.NotFound();

        // Only Active or Testing sources serve the form
        if (source.Status is not (LeadSourceStatus.Active or LeadSourceStatus.Testing))
            return Results.NotFound();

        var settings = source.Settings as EmbeddedScriptSettings;
        if (settings is null)
            return Results.NotFound();

        // Origin check
        var origin = http.Request.Headers.Origin.FirstOrDefault()
                  ?? http.Request.Headers.Referer.FirstOrDefault();

        if (origin is not null && settings.AllowedOrigins.Count > 0)
        {
            var allowed = settings.AllowedOrigins.Any(ao =>
                origin.Contains(ao, StringComparison.OrdinalIgnoreCase));

            if (!allowed)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Build response — only public data
        var response = new WebFormResponse(
            Fields: settings.FormFields?
                .OrderBy(f => f.Order)
                .Select(f => new WebFormFieldDto(
                    f.Name, f.Label, f.Type, f.IsRequired, f.Placeholder, f.Options))
                .ToList() ?? [],
            ConsentText:    settings.ConsentText,
            ConsentVersion: settings.ConsentVersion,
            Theme:          settings.Theme,
            SubmitLabel:    settings.SubmitButtonLabel ?? "Submit",
            CaptchaProvider: settings.CaptchaProvider);

        // ETag based on settings content hash
        var etag = ComputeETag(source.Code, settings.SchemaVersion, settings.FormFields?.Count ?? 0);

        if (http.Request.Headers.IfNoneMatch == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "public, max-age=300";

        return Results.Ok(response);
    }

    private static string ComputeETag(string code, int schemaVersion, int fieldCount)
    {
        var input = $"{code}:{schemaVersion}:{fieldCount}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"\"{Convert.ToHexString(hash[..8])}\"";
    }
}

public sealed record WebFormResponse(
    IReadOnlyList<WebFormFieldDto> Fields,
    string? ConsentText,
    string? ConsentVersion,
    string? Theme,
    string SubmitLabel,
    string? CaptchaProvider);

public sealed record WebFormFieldDto(
    string Name,
    string Label,
    string Type,
    bool IsRequired,
    string? Placeholder,
    IReadOnlyList<string>? Options);
