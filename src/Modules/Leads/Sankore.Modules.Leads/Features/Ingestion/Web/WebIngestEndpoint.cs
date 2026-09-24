namespace Sankore.Modules.Leads.Features.Ingestion.Web;

using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Public endpoint for embedded script form submissions (US-F13.37-BE-12).
/// No JWT required — authentication is via source PublicKey.
/// </summary>
public static class WebIngestEndpoint
{
    private const int MaxBodyBytes = 32 * 1024; // 32 KB

    public static IEndpointRouteBuilder MapWebIngestEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/web/{publicKey}", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting("ingest-key")
            .WithName("WebIngest")
            .WithTags("Ingest")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string publicKey,
        HttpContext httpContext,
        LeadsDbContext db,
        ISender sender,
        ICaptchaValidator captchaValidator,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("WebIngest");

        // ── 1. Body size check ──────────────────────────────────────────
        if (httpContext.Request.ContentLength > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // ── 2. Read body with size limit ────────────────────────────────
        httpContext.Request.EnableBuffering();
        using var reader = new StreamReader(httpContext.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (body.Length > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // ── 3. Resolve source by PublicKey ───────────────────────────────
        var source = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.PublicKey == publicKey, ct);

        if (source is null || source.Status == LeadSourceStatus.Archived)
            return Results.NotFound();

        if (source.Status == LeadSourceStatus.Paused)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (source.Status is not (LeadSourceStatus.Active or LeadSourceStatus.Testing))
            return Results.NotFound();

        // ── 4. Origin check ─────────────────────────────────────────────
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
            {
                logger.LogWarning("Web ingest origin rejected: {Origin} for key {PublicKey}", origin, publicKey);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Set CORS headers for the response
            httpContext.Response.Headers.Append("Access-Control-Allow-Origin", origin);
            httpContext.Response.Headers.Append("Access-Control-Allow-Methods", "POST, OPTIONS");
            httpContext.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type");
        }

        // ── 5. Parse request body ───────────────────────────────────────
        WebIngestRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<WebIngestRequest>(body, JsonOpts);
        }
        catch
        {
            return Results.UnprocessableEntity(new { error = "INVALID_JSON" });
        }

        if (request is null)
            return Results.UnprocessableEntity(new { error = "EMPTY_BODY" });

        // ── 6. Honeypot check (silent reject) ───────────────────────────
        if (settings?.HoneypotFieldName is not null
            && request.Fields.TryGetValue(settings.HoneypotFieldName, out var honeypot)
            && !string.IsNullOrEmpty(honeypot))
        {
            logger.LogWarning("Web ingest honeypot triggered for key {PublicKey} from {Ip}",
                publicKey, GetIp(httpContext));
            return Results.Accepted();
        }

        // ── 7. Submit delay check (silent reject) ───────────────────────
        if (settings?.MinSubmitDelaySeconds > 0 && request.FormLoadedAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - request.FormLoadedAt.Value;
            if (elapsed.TotalSeconds < settings.MinSubmitDelaySeconds)
            {
                logger.LogWarning("Web ingest submit too fast ({Elapsed}s) for key {PublicKey} from {Ip}",
                    elapsed.TotalSeconds, publicKey, GetIp(httpContext));
                return Results.Accepted();
            }
        }

        // ── 8. Consent check ────────────────────────────────────────────
        if (request.Consent != true)
            return Results.UnprocessableEntity(new { error = "ConsentRequired" });

        // ── 9. Captcha check ────────────────────────────────────────────
        if (settings?.CaptchaProvider is not null)
        {
            if (string.IsNullOrEmpty(request.CaptchaToken))
                return Results.UnprocessableEntity(new { error = "CaptchaFailed" });

            var captchaValid = await captchaValidator.ValidateAsync(
                settings.CaptchaProvider, request.CaptchaToken, GetIp(httpContext), ct);

            if (!captchaValid)
                return Results.UnprocessableEntity(new { error = "CaptchaFailed" });
        }

        // ── 10. Build enriched payload ──────────────────────────────────
        var externalId = Guid.NewGuid().ToString();
        var enrichedFields = new Dictionary<string, string?>(request.Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["_ip"] = GetIp(httpContext),
            ["_userAgent"] = httpContext.Request.Headers.UserAgent.ToString(),
            ["_page"] = request.Page,
            ["_referrer"] = request.Referrer,
            ["_consentVersion"] = request.ConsentVersion,
            ["_origin"] = origin,
        };
        var rawPayload = JsonSerializer.Serialize(enrichedFields, JsonOpts);

        // ── 11. Ingest via unified pipeline ─────────────────────────────
        var result = await sender.Send(new IngestInboundLeadCommand(
            TenantId:       source.TenantId,
            SourceId:       source.Id,
            RawPayloadJson: rawPayload,
            ExternalId:     externalId), ct);

        if (result.IsFailure)
            return Results.UnprocessableEntity(new { error = result.Error });

        return Results.Accepted(value: new { ingestionId = result.Value.IngestionId });
    }

    private static string GetIp(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>Web form submission request body.</summary>
public sealed record WebIngestRequest
{
    public Dictionary<string, string?> Fields { get; init; } = new();
    public bool? Consent { get; init; }
    public string? ConsentVersion { get; init; }
    public string? CaptchaToken { get; init; }
    public DateTimeOffset? FormLoadedAt { get; init; }
    public string? Page { get; init; }
    public string? Referrer { get; init; }
}
