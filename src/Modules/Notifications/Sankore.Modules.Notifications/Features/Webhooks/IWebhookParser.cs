namespace Sankore.Modules.Notifications.Features.Webhooks;

using Sankore.Modules.Notifications.Domain;

/// <summary>
/// Provider-specific webhook payload parser.
/// One implementation per supported email provider (SES/SNS, Postmark, SendGrid).
/// </summary>
internal interface IWebhookParser
{
    /// <summary>Matches the "provider" query-param value (lower-case): "ses", "postmark", "sendgrid".</summary>
    string ProviderKey { get; }

    /// <summary>
    /// Parses the raw HTTP body and returns zero or more delivery events.
    /// Returns empty list for unrecognised or irrelevant payloads.
    /// Must never throw — callers always return HTTP 200 to prevent provider retries.
    /// </summary>
    IReadOnlyList<ParsedWebhookEvent> Parse(string rawBody);
}

/// <summary>A normalised delivery event extracted from a provider webhook payload.</summary>
internal sealed record ParsedWebhookEvent(
    EmailDeliveryEventType EventType,
    string RecipientEmail,
    /// <summary>Provider message-ID — used for future outbox correlation.</summary>
    string? ExternalMessageId = null);
