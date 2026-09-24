namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

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
    private const string SignatureHeader = "X-Sankore-Signature";

    public static IEndpointRouteBuilder MapWebhookIngestEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/hooks/{publicKey}", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting("ingest-key")
            .WithName("WebhookIngest")
            .WithTags("Ingest")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string publicKey,
        HttpContext httpContext,
        ISender sender,
        CancellationToken ct)
    {
        httpContext.Request.EnableBuffering();
        using var reader = new StreamReader(httpContext.Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var result = await sender.Send(new IngestWebhookCommand(
            PublicKey:       publicKey,
            Body:            body,
            SignatureHeader: httpContext.Request.Headers[SignatureHeader].ToString(),
            RemoteIp:        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"), ct);

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
}
