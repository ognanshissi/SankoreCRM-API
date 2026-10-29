namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using System.Text;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Thin HTTP adapter for webhook ingestion (US-F13.37-BE-17).
/// All domain logic lives in <see cref="IngestWebhookHandler"/>.
/// </summary>
public static class WebhookIngestEndpoint
{
    /// <summary>
    /// 100 items of a few hundred bytes each is ~50 KB; 256 KB leaves room for verbose providers.
    /// The body used to be read whole, whatever its size, by an anonymous route.
    /// </summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>
    /// A server pushes in bursts and batches; the web form's 10 a minute (<c>ingest-key</c>) cut
    /// a provider off in the middle of a busy hour. Declared by the host (Program.cs).
    /// </summary>
    public const string RateLimitPolicy = "ingest-hook";

    public static IEndpointRouteBuilder MapWebhookIngestEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/hooks/{publicKey}", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("WebhookIngest")
            .WithTags("Ingest")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string publicKey,
        HttpContext httpContext,
        ISender sender,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(httpContext, ct);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var signatureHeaders = SignatureHeaders(httpContext);

        var result = await sender.Send(new IngestWebhookCommand(
            PublicKey:        publicKey,
            Body:             body,
            SignatureHeaders: signatureHeaders,
            RemoteIp:         httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"), ct);

        if (result.IsFailure)
            return Results.Problem(detail: result.Error, statusCode: 500);

        var v = result.Value;

        if (v.StatusCode != 202)
            return v.Items.Count > 0
                ? Results.Json(v.Items[0], statusCode: v.StatusCode)
                : Results.StatusCode(v.StatusCode);

        if (v.Items.Count == 1)
            return Results.Json(v.Items[0], statusCode: 202);

        return Results.Json(new { items = v.Items }, statusCode: 202);
    }

    /// <summary>The body as text, or null when it exceeds <see cref="MaxBodyBytes"/>.</summary>
    internal static async Task<string?> ReadBodyAsync(HttpContext httpContext, CancellationToken ct)
    {
        if (httpContext.Request.ContentLength > MaxBodyBytes)
            return null;

        // Read at most one byte past the limit: a chunked body announces no length.
        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await httpContext.Request.Body.ReadAsync(buffer.AsMemory(read), ct)) > 0)
            read += n;

        return read > MaxBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>Every header naming a signature: which one counts is the source's setting.</summary>
    internal static IReadOnlyDictionary<string, string> SignatureHeaders(HttpContext httpContext)
        => httpContext.Request.Headers
            .Where(h => h.Key.Contains("signature", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
}
