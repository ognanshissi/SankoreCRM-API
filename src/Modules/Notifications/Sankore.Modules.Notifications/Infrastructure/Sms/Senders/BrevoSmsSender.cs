namespace Sankore.Modules.Notifications.Infrastructure.Sms.Senders;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Brevo's transactional SMS API (<c>POST /v3/transactionalSMS/sms</c>).
///
/// <para>
/// The same shape as <c>BrevoEmailSender</c> next door — a named HTTP client, the key pulled from
/// the vault per send and never cached, the provider's own error body carried into the exception
/// — because the two are the same provider and an operator debugging one should recognise the
/// other.
/// </para>
/// </summary>
internal sealed class BrevoSmsSender(
    IHttpClientFactory httpClientFactory,
    ISmsCredentials credentials,
    ILogger<BrevoSmsSender> logger) : ISmsSender
{
    /// <summary>
    /// Its own named client, separate from the email one: the two can need different timeouts —
    /// an SMS send is the slower of the two — and a host that wants a proxy on one may not want
    /// it on the other.
    /// </summary>
    public const string HttpClientName = "brevo-sms";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<SmsSendResult> SendAsync(
        SendSmsRequest request, ResolvedSmsProvider provider, CancellationToken ct)
    {
        var apiKey = await credentials.GetAsync(request.TenantId, provider, ct);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Throwing hands the message back to the outbox, which retries and dead-letters.
            // Returning normally would record it as sent.
            throw new InvalidOperationException(
                "Brevo is selected for this tenant's SMS channel but no API key is in the vault.");
        }

        if (string.IsNullOrWhiteSpace(provider.SenderId))
        {
            throw new InvalidOperationException(
                "Brevo requires a sender name; none is configured for this tenant.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri("https://api.brevo.com/");

        using var message = new HttpRequestMessage(HttpMethod.Post, "v3/transactionalSMS/sms")
        {
            Content = JsonContent.Create(
                new BrevoSms(
                    Sender: provider.SenderId,
                    Recipient: request.ToPhone,
                    Content: request.Body,
                    // "transactional", so Brevo does not apply its marketing unsubscribe rules
                    // and does not refuse the message outside business hours. Every message this
                    // platform sends is transactional by construction: a one-time code, an
                    // outcome. There is no marketing path here, and there must not be one added
                    // without a consent record to go with it.
                    Type: "transactional",
                    // Correlates the provider's own logs with ours. Brevo echoes it back on its
                    // delivery webhook, which is what lets a callback find the outbox row even if
                    // the message id is missing.
                    Tag: request.MessageId.ToString("N")),
                options: JsonOptions),
        };

        // Brevo authenticates on this header, not on Authorization.
        message.Headers.Add("api-key", apiKey);

        using var response = await client.SendAsync(message, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            // Brevo's own error names the cause — unapproved sender, invalid number, no credit —
            // and that is the sentence worth having in a dead-letter row. Truncated because it
            // ends up in a log line.
            throw new InvalidOperationException(
                $"Brevo rejected the SMS ({(int)response.StatusCode}): "
                + body[..Math.Min(body.Length, 500)]);
        }

        var receipt = await response.Content.ReadFromJsonAsync<BrevoSmsReceipt>(JsonOptions, ct);

        logger.LogInformation(
            "Brevo SMS accepted | MessageId={Id} Reference={Reference} Credits={Credits}",
            request.MessageId, receipt?.Reference, receipt?.UsedCredits);

        return new SmsSendResult(receipt?.Reference, "accepted");
    }

    private sealed record BrevoSms(
        [property: JsonPropertyName("sender")] string Sender,
        [property: JsonPropertyName("recipient")] string Recipient,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("tag")] string Tag);

    private sealed record BrevoSmsReceipt(
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("usedCredits")] double? UsedCredits);
}
