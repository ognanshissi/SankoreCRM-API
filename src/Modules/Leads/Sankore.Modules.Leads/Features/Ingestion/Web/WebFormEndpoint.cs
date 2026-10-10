using Microsoft.Extensions.DependencyInjection;

namespace Sankore.Modules.Leads.Features.Ingestion.Web;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            CaptchaProvider: settings.CaptchaProviderName,
            AccentColor:    settings.AccentColor,
            FontFamily:     settings.FontFamily);

        var etag = ComputeETag(source.Code, response);

        if (http.Request.Headers.IfNoneMatch == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "public, max-age=300";

        return Results.Ok(response);
    }

    /// <summary>
    /// Hashes the RESPONSE, not a summary of it.
    ///
    /// <para>
    /// It used to hash the source code, the schema version and the FIELD COUNT, which meant
    /// every change that kept the number of fields the same was invisible to a client holding
    /// the old ETag: a relabelled field, a reworded consent notice, a new submit label — and now
    /// an accent colour — all answered 304 and the form never changed. Hashing the serialised
    /// response makes the tag change exactly when the form does.
    /// </para>
    /// </summary>
    private static string ComputeETag(string code, WebFormResponse response)
    {
        var input = $"{code}:{JsonSerializer.Serialize(response, EtagJsonOptions)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"\"{Convert.ToHexString(hash[..8])}\"";
    }

    /// <summary>
    /// Fixed options, so the tag depends on the response and not on how the host happens to be
    /// configured to serialise it.
    /// </summary>
    private static readonly JsonSerializerOptions EtagJsonOptions = new();
}

public sealed record WebFormResponse(
    IReadOnlyList<WebFormFieldDto> Fields,
    string? ConsentText,
    string? ConsentVersion,
    string? Theme,
    string SubmitLabel,
    string? CaptchaProvider,
    /// <summary>
    /// Accent colour the SDK uses as the FALLBACK of <c>--sankore-primary</c>, so the tenant's
    /// choice applies and a host site can still override it with that CSS variable. Null leaves
    /// the SDK's own default.
    /// </summary>
    string? AccentColor = null,
    /// <summary>Font stack, fallback of <c>--sankore-font</c>. Same contract as the colour.</summary>
    string? FontFamily = null);

public sealed record WebFormFieldDto(
    string Name,
    string Label,
    string Type,
    bool IsRequired,
    string? Placeholder,
    IReadOnlyList<string>? Options);
