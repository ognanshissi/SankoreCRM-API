namespace Sankore.Modules.Notifications.Features.Webhooks;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Features.Webhooks.ReceiveEmailWebhook;

internal static class WebhooksEndpoints
{
    internal static IEndpointRouteBuilder MapWebhooksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("webhooks").WithTags("Webhooks");

        // POST /webhooks/email/{tenantId}?provider=ses|postmark|sendgrid
        // Anonymous — called by external email providers, no JWT.
        group.MapPost("email/{tenantId:guid}", async (
            Guid tenantId,
            string provider,
            HttpRequest httpRequest,
            ISender sender,
            IHttpClientFactory httpClientFactory,
            ILogger<WebhooksEndpointsMarker> logger,
            CancellationToken ct) =>
        {
            httpRequest.EnableBuffering();
            var rawBody = await new StreamReader(httpRequest.Body).ReadToEndAsync(ct);

            // ── AWS SNS SubscriptionConfirmation handshake ──────────────────
            if (string.Equals(provider, "ses", StringComparison.OrdinalIgnoreCase)
                && rawBody.Contains("\"SubscriptionConfirmation\"", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
                    if (doc.RootElement.TryGetProperty("SubscribeURL", out var urlProp))
                    {
                        var subscribeUrl = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(subscribeUrl))
                        {
                            var client = httpClientFactory.CreateClient();
                            await client.GetAsync(subscribeUrl, ct);
                            logger.LogInformation(
                                "SNS SubscriptionConfirmation confirmed for tenant {TenantId}", tenantId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to confirm SNS subscription for tenant {TenantId}", tenantId);
                }

                return Results.Ok();
            }

            // ── Normal delivery event ────────────────────────────────────────
            await sender.Send(new ReceiveEmailWebhookCommand(tenantId, provider, rawBody), ct);

            // Always 200 — prevents provider from retrying valid payloads we chose to ignore
            return Results.Ok();
        })
        .AllowAnonymous()
        .WithName("ReceiveEmailWebhook");

        return app;
    }

    // Marker type for ILogger category — avoids open generic logger issues
    private sealed class WebhooksEndpointsMarker;
}
