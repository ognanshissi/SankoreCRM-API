namespace Sankore.Modules.Notifications.Features.Webhooks.Parsers;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;

/// <summary>
/// Parses SendGrid event webhook payloads (array format).
///
/// Payload is a JSON array; each element:
///   { "event": "delivered"|"bounce"|"dropped"|"spamreport"|"open"|"click"|...,
///     "email": "...",
///     "sg_message_id": "..." }
///
/// Only delivery-outcome events are recorded (open/click/unsubscribe are ignored).
/// </summary>
internal sealed class SendGridWebhookParser(ILogger<SendGridWebhookParser> logger) : IWebhookParser
{
    public string ProviderKey => "sendgrid";

    public IReadOnlyList<ParsedWebhookEvent> Parse(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);

            // SendGrid sends an array
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<ParsedWebhookEvent>();

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var eventType = element.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                var email = element.TryGetProperty("email", out var em) ? em.GetString() : null;
                var msgId = element.TryGetProperty("sg_message_id", out var mid) ? mid.GetString() : null;

                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(eventType))
                    continue;

                var mapped = MapEventType(eventType);
                if (mapped is null) continue; // open/click/etc — not a delivery outcome

                results.Add(new ParsedWebhookEvent(mapped.Value, email, msgId));
            }

            return results;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SendGridWebhookParser failed to parse payload");
            return [];
        }
    }

    private static EmailDeliveryEventType? MapEventType(string eventType) => eventType.ToLowerInvariant() switch
    {
        "delivered" => EmailDeliveryEventType.Delivered,
        "bounce" => EmailDeliveryEventType.Bounced,
        "dropped" => EmailDeliveryEventType.Rejected,
        "spamreport" => EmailDeliveryEventType.Complained,
        _ => null
    };
}
