namespace Sankore.Modules.Notifications.Features.Webhooks.Parsers;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;

/// <summary>
/// Parses AWS SES delivery events wrapped in an SNS Notification message.
///
/// Outer SNS envelope:
///   { "Type": "Notification", "Message": "&lt;inner-json&gt;" }
///
/// Inner SES event:
///   { "notificationType": "Delivery"|"Bounce"|"Complaint"|"Reject",
///     "mail": { "messageId": "...", "destination": ["..."] },
///     "delivery":   { "recipients": ["..."] },
///     "bounce":     { "bouncedRecipients": [{"emailAddress":"..."}] },
///     "complaint":  { "complainedRecipients": [{"emailAddress":"..."}] } }
///
/// SubscriptionConfirmation payloads are intentionally not parsed here —
/// they are intercepted at the endpoint level before this parser is called.
/// </summary>
internal sealed class SnsWebhookParser(ILogger<SnsWebhookParser> logger) : IWebhookParser
{
    public string ProviderKey => "ses";

    public IReadOnlyList<ParsedWebhookEvent> Parse(string rawBody)
    {
        try
        {
            using var outer = JsonDocument.Parse(rawBody);
            var root = outer.RootElement;

            if (!root.TryGetProperty("Message", out var messageProp))
                return [];

            var messageJson = messageProp.GetString();
            if (string.IsNullOrWhiteSpace(messageJson))
                return [];

            using var inner = JsonDocument.Parse(messageJson);
            var msg = inner.RootElement;

            var notificationType = msg.TryGetProperty("notificationType", out var nt)
                ? nt.GetString() ?? string.Empty
                : string.Empty;

            var externalId = msg.TryGetProperty("mail", out var mail)
                && mail.TryGetProperty("messageId", out var mid)
                ? mid.GetString()
                : null;

            return notificationType switch
            {
                "Delivery" => ParseDelivery(msg, externalId),
                "Bounce" => ParseBounce(msg, externalId),
                "Complaint" => ParseComplaint(msg, externalId),
                "Reject" => ParseReject(msg, externalId),
                _ => []
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SnsWebhookParser failed to parse payload");
            return [];
        }
    }

    private static IReadOnlyList<ParsedWebhookEvent> ParseDelivery(JsonElement msg, string? externalId)
    {
        var recipients = GetRecipients(msg, "delivery", "recipients");
        return recipients.Select(r => new ParsedWebhookEvent(EmailDeliveryEventType.Delivered, r, externalId)).ToList();
    }

    private static IReadOnlyList<ParsedWebhookEvent> ParseBounce(JsonElement msg, string? externalId)
    {
        var recipients = GetEmailAddresses(msg, "bounce", "bouncedRecipients", "emailAddress");
        return recipients.Select(r => new ParsedWebhookEvent(EmailDeliveryEventType.Bounced, r, externalId)).ToList();
    }

    private static IReadOnlyList<ParsedWebhookEvent> ParseComplaint(JsonElement msg, string? externalId)
    {
        var recipients = GetEmailAddresses(msg, "complaint", "complainedRecipients", "emailAddress");
        return recipients.Select(r => new ParsedWebhookEvent(EmailDeliveryEventType.Complained, r, externalId)).ToList();
    }

    private static IReadOnlyList<ParsedWebhookEvent> ParseReject(JsonElement msg, string? externalId)
    {
        // Rejected: fall back to mail.destination for recipient list
        var emails = msg.TryGetProperty("mail", out var mail)
                     && mail.TryGetProperty("destination", out var dest)
            ? dest.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
            : [];
        return emails.Select(r => new ParsedWebhookEvent(EmailDeliveryEventType.Rejected, r, externalId)).ToList();
    }

    private static List<string> GetRecipients(JsonElement msg, string section, string arrayProp)
    {
        if (!msg.TryGetProperty(section, out var sec)) return [];
        if (!sec.TryGetProperty(arrayProp, out var arr)) return [];
        return arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList();
    }

    private static List<string> GetEmailAddresses(
        JsonElement msg, string section, string arrayProp, string emailProp)
    {
        if (!msg.TryGetProperty(section, out var sec)) return [];
        if (!sec.TryGetProperty(arrayProp, out var arr)) return [];
        return arr.EnumerateArray()
            .Select(e => e.TryGetProperty(emailProp, out var ep) ? ep.GetString() ?? string.Empty : string.Empty)
            .Where(s => s.Length > 0)
            .ToList();
    }
}
