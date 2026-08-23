namespace Sankore.Modules.Notifications.Features.Webhooks.Parsers;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;

/// <summary>
/// Parses Postmark delivery webhook payloads.
///
/// Delivery:    { "RecordType": "Delivery",     "MessageID": "...", "Recipient": "..." }
/// Bounce:      { "RecordType": "Bounce",        "MessageID": "...", "Email": "..." }
/// Complaint:   { "RecordType": "SpamComplaint", "MessageID": "...", "Email": "..." }
/// Open/Click:  ignored (not delivery-outcome events).
/// </summary>
internal sealed class PostmarkWebhookParser(ILogger<PostmarkWebhookParser> logger) : IWebhookParser
{
    public string ProviderKey => "postmark";

    public IReadOnlyList<ParsedWebhookEvent> Parse(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            var recordType = root.TryGetProperty("RecordType", out var rt)
                ? rt.GetString() ?? string.Empty
                : string.Empty;

            var messageId = root.TryGetProperty("MessageID", out var mid) ? mid.GetString() : null;

            return recordType switch
            {
                "Delivery" => ParseSingle(root, "Recipient", EmailDeliveryEventType.Delivered, messageId),
                "Bounce" => ParseSingle(root, "Email", EmailDeliveryEventType.Bounced, messageId),
                "SpamComplaint" => ParseSingle(root, "Email", EmailDeliveryEventType.Complained, messageId),
                _ => []
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostmarkWebhookParser failed to parse payload");
            return [];
        }
    }

    private static IReadOnlyList<ParsedWebhookEvent> ParseSingle(
        JsonElement root, string emailProp, EmailDeliveryEventType eventType, string? externalId)
    {
        var email = root.TryGetProperty(emailProp, out var ep) ? ep.GetString() : null;
        if (string.IsNullOrWhiteSpace(email)) return [];
        return [new ParsedWebhookEvent(eventType, email, externalId)];
    }
}
