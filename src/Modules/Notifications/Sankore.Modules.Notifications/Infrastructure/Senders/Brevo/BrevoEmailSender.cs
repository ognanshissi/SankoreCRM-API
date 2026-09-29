namespace Sankore.Modules.Notifications.Infrastructure.Senders.Brevo;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Infrastructure.Providers;

/// <summary>
/// Sends through Brevo's transactional API (<c>POST /v3/smtp/email</c>).
///
/// The HTTP API is used rather than Brevo's SMTP relay because it reports a per-message id and a
/// structured error, which is what the delivery log and the webhook correlation need. A tenant
/// who prefers the relay can still configure Brevo's SMTP host under the "Smtp" provider.
///
/// The API key is fetched from the vault per send, never cached.
/// </summary>
internal sealed class BrevoEmailSender(
    IHttpClientFactory httpClientFactory,
    INotificationCredentials credentials,
    ILogger<BrevoEmailSender> logger) : IEmailSender
{
    /// <summary>Named client so a host can add its own handlers, timeouts or proxy.</summary>
    public const string HttpClientName = "brevo";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SendAsync(SendEmailRequest request, ResolvedEmailProvider provider, CancellationToken ct)
    {
        var apiKey = await credentials.GetAsync(request.TenantId, provider, ct);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Throwing hands the message back to the outbox, which retries and eventually
            // dead-letters it. Silently doing nothing would report it as delivered.
            throw new InvalidOperationException(
                "Brevo is selected for this tenant but no API key is stored in the vault.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri("https://api.brevo.com/");

        using var message = new HttpRequestMessage(HttpMethod.Post, "v3/smtp/email")
        {
            Content = JsonContent.Create(new BrevoEmail(
                Sender: new BrevoContact(request.FromEmail, request.FromName),
                To: [new BrevoContact(request.ToEmail, request.ToName)],
                ReplyTo: string.IsNullOrWhiteSpace(request.ReplyToEmail)
                    ? null
                    : new BrevoContact(request.ReplyToEmail, null),
                Subject: request.Subject,
                HtmlContent: request.HtmlBody,
                TextContent: request.TextBody),
                options: JsonOptions),
        };

        // Brevo authenticates on this header, not on Authorization.
        message.Headers.Add("api-key", apiKey);

        using var response = await client.SendAsync(message, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            // The body is Brevo's own error, which names the cause (unknown sender, quota,
            // invalid key). Truncated because it ends up in a log line and a delivery record.
            throw new InvalidOperationException(
                $"Brevo rejected the message ({(int)response.StatusCode}): "
                + body[..Math.Min(body.Length, 500)]);
        }

        var receipt = await response.Content.ReadFromJsonAsync<BrevoReceipt>(JsonOptions, ct);

        logger.LogInformation(
            "Brevo email sent | From={From} To={To} MessageId={Id} BrevoId={BrevoId}",
            request.FromEmail, request.ToEmail, request.MessageId, receipt?.MessageId);
    }

    private sealed record BrevoEmail(
        [property: JsonPropertyName("sender")] BrevoContact Sender,
        [property: JsonPropertyName("to")] IReadOnlyList<BrevoContact> To,
        [property: JsonPropertyName("replyTo")] BrevoContact? ReplyTo,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("htmlContent")] string HtmlContent,
        [property: JsonPropertyName("textContent")] string? TextContent);

    private sealed record BrevoContact(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record BrevoReceipt(
        [property: JsonPropertyName("messageId")] string? MessageId);
}
