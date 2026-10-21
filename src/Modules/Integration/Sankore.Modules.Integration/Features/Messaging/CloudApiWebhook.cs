namespace Sankore.Modules.Integration.Features.Messaging;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Sankore.Modules.Integration.Ports;

/// <summary>
/// Reads the webhook bodies of Meta's WhatsApp Cloud API (WA-BE-07, WA-BE-08).
///
/// <para>
/// <b>Shared, and not inside one adapter</b>, because the format is Meta's and not the
/// provider's: 360dialog forwards Cloud API bodies untouched, the in-memory double speaks the same
/// shape so the simulator exercises the real reader, and a direct Meta connection would read them
/// too. One reader means one place a field name can be wrong.
/// </para>
///
/// <para>
/// <b>Pure, and it never throws on input.</b> A body that is not JSON, or JSON of another shape,
/// answers an empty batch: it reached us from outside, and an exception would turn a malformed
/// delivery into a Hangfire retry loop over the same bytes.
/// </para>
/// </summary>
public static class CloudApiWebhook
{
    /// <summary>
    /// The phone-number ids a body names, read <b>before</b> the body is verified — the one read
    /// that has to be, because the id is how the endpoint finds the connection whose secret
    /// verifies it.
    ///
    /// <para>
    /// Deliberately not a deserialisation. A forward-only token scan for the string values of a
    /// <c>phone_number_id</c> property, with a depth limit and no object built: an unverified
    /// caller can make this read bytes and nothing else. Anything malformed answers empty, which
    /// the endpoint refuses like any other unroutable body.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ReadPhoneNumberIds(ReadOnlySpan<byte> body)
    {
        var found = new List<string>(1);

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
            });

            var expectingValue = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    expectingValue = reader.ValueTextEquals("phone_number_id"u8);
                    continue;
                }

                if (expectingValue && reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString();

                    if (!string.IsNullOrWhiteSpace(value) && !found.Contains(value, StringComparer.Ordinal))
                        found.Add(value);
                }

                expectingValue = false;

                // Ten distinct numbers in one body is already more than a real delivery carries;
                // a body naming hundreds is somebody probing, and reading on costs only them.
                if (found.Count > 10) return [];
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return found;
    }

    /// <summary>
    /// Everything the body says: the inbound messages and the delivery statuses, each tagged with
    /// the phone-number id of the change that carried it.
    /// </summary>
    public static ConversationalWebhookBatch Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ConversationalWebhookBatch.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            return Read(doc.RootElement);
        }
        catch (JsonException)
        {
            return ConversationalWebhookBatch.Empty;
        }
    }

    private static ConversationalWebhookBatch Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return ConversationalWebhookBatch.Empty;
        }

        var numbers = new List<string>();
        var messages = new List<ProviderInboundMessage>();
        var statuses = new List<ProviderMessageStatus>();

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                    continue;

                var phoneNumberId = String(value, "metadata", "phone_number_id");
                if (string.IsNullOrWhiteSpace(phoneNumberId)) continue;

                if (!numbers.Contains(phoneNumberId, StringComparer.Ordinal))
                    numbers.Add(phoneNumberId);

                var profileNames = ProfileNames(value);

                if (value.TryGetProperty("messages", out var inbound) && inbound.ValueKind == JsonValueKind.Array)
                {
                    foreach (var message in inbound.EnumerateArray())
                    {
                        if (ReadMessage(phoneNumberId, message, profileNames) is { } read)
                            messages.Add(read);
                    }
                }

                if (value.TryGetProperty("statuses", out var outbound) && outbound.ValueKind == JsonValueKind.Array)
                {
                    foreach (var status in outbound.EnumerateArray())
                    {
                        if (ReadStatus(phoneNumberId, status) is { } read)
                            statuses.Add(read);
                    }
                }
            }
        }

        return new ConversationalWebhookBatch(numbers, messages, statuses);
    }

    private static ProviderInboundMessage? ReadMessage(
        string phoneNumberId, JsonElement message, IReadOnlyDictionary<string, string> profileNames)
    {
        var id = String(message, "id");
        var from = String(message, "from");
        var timestamp = Timestamp(message);

        // A message without its id cannot be made idempotent, and one without a sender cannot be
        // attached to anybody. Dropped, not guessed.
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from) || timestamp is null)
            return null;

        var type = NormaliseType(String(message, "type"));

        string? text = null;
        string? mediaId = null;
        string? mime = null;
        string? fileName = null;

        switch (type)
        {
            case "text":
                text = String(message, "text", "body");
                break;

            case "image" or "document" or "audio" or "video" or "sticker":
                mediaId = String(message, type, "id");
                mime = String(message, type, "mime_type");
                fileName = String(message, type, "filename");
                text = String(message, type, "caption");
                break;

            case "button":
                // A quick-reply button of a template: what the customer pressed is its label.
                text = String(message, "button", "text");
                break;

            case "interactive":
                text = String(message, "interactive", "button_reply", "title")
                       ?? String(message, "interactive", "list_reply", "title");
                break;

            case "location":
                text = String(message, "location", "name") ?? String(message, "location", "address");
                break;

            case "reaction":
                text = String(message, "reaction", "emoji");
                break;
        }

        var sender = from.StartsWith('+') ? from : "+" + from;

        return new ProviderInboundMessage(
            PhoneNumberId: phoneNumberId,
            ProviderMessageId: id,
            From: sender,
            ProfileName: profileNames.TryGetValue(from.TrimStart('+'), out var name) ? name : null,
            Timestamp: timestamp.Value,
            Type: type,
            Text: text,
            MediaId: mediaId,
            MediaMimeType: mime,
            MediaFileName: fileName,
            ReplyToProviderMessageId: String(message, "context", "id"));
    }

    private static ProviderMessageStatus? ReadStatus(string phoneNumberId, JsonElement status)
    {
        var id = String(status, "id");
        var word = String(status, "status");
        var timestamp = Timestamp(status);

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(word) || timestamp is null)
            return null;

        string? code = null;
        string? title = null;

        if (status.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var first = errors[0];

            code = first.TryGetProperty("code", out var c)
                ? c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.ToString()
                : null;

            // The title is Meta's sentence, in English, about the failure — never about the
            // customer. Kept for the agent who sees the message fail.
            title = String(first, "title") ?? String(first, "message");
        }

        return new ProviderMessageStatus(
            phoneNumberId, id, word.ToLowerInvariant(), timestamp.Value, code, Truncate(title, 300));
    }

    private static IReadOnlyDictionary<string, string> ProfileNames(JsonElement value)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!value.TryGetProperty("contacts", out var contacts) || contacts.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var contact in contacts.EnumerateArray())
        {
            var waId = String(contact, "wa_id");
            var name = String(contact, "profile", "name");

            if (!string.IsNullOrWhiteSpace(waId) && !string.IsNullOrWhiteSpace(name))
                names[waId.TrimStart('+')] = Truncate(name, 200)!;
        }

        return names;
    }

    private static string NormaliseType(string? type) => type?.ToLowerInvariant() switch
    {
        "text" or "image" or "document" or "audio" or "video" or "sticker" or "location"
            or "contacts" or "button" or "interactive" or "reaction" => type.ToLowerInvariant(),
        _ => "unsupported",
    };

    /// <summary>Meta sends Unix seconds, as a string.</summary>
    private static DateTimeOffset? Timestamp(JsonElement element)
    {
        if (!element.TryGetProperty("timestamp", out var raw)) return null;

        var text = raw.ValueKind == JsonValueKind.Number ? raw.GetRawText() : raw.GetString();

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
               && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    private static string? String(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    /// <summary>
    /// Builds a Cloud API body for ONE inbound text message — what the development simulator
    /// posts, so a made-up message travels the exact path a real one does.
    /// </summary>
    public static string BuildInboundText(
        string phoneNumberId, string fromE164, string? profileName, string text, string messageId, DateTimeOffset at)
    {
        var digits = fromE164.TrimStart('+');

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("object", "whatsapp_business_account");
            w.WriteStartArray("entry");
            w.WriteStartObject();
            w.WriteString("id", "simulated");
            w.WriteStartArray("changes");
            w.WriteStartObject();
            w.WriteString("field", "messages");
            w.WriteStartObject("value");
            w.WriteString("messaging_product", "whatsapp");
            w.WriteStartObject("metadata");
            w.WriteString("phone_number_id", phoneNumberId);
            w.WriteEndObject();
            w.WriteStartArray("contacts");
            w.WriteStartObject();
            w.WriteString("wa_id", digits);
            w.WriteStartObject("profile");
            w.WriteString("name", profileName ?? "Contact");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("from", digits);
            w.WriteString("id", messageId);
            w.WriteString("timestamp", at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            w.WriteString("type", "text");
            w.WriteStartObject("text");
            w.WriteString("body", text);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Same, for one delivery status — what the simulator posts for « livré » / « lu ».</summary>
    public static string BuildStatus(
        string phoneNumberId, string providerMessageId, string status, DateTimeOffset at)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("object", "whatsapp_business_account");
            w.WriteStartArray("entry");
            w.WriteStartObject();
            w.WriteString("id", "simulated");
            w.WriteStartArray("changes");
            w.WriteStartObject();
            w.WriteString("field", "messages");
            w.WriteStartObject("value");
            w.WriteString("messaging_product", "whatsapp");
            w.WriteStartObject("metadata");
            w.WriteString("phone_number_id", phoneNumberId);
            w.WriteEndObject();
            w.WriteStartArray("statuses");
            w.WriteStartObject();
            w.WriteString("id", providerMessageId);
            w.WriteString("status", status);
            w.WriteString("timestamp", at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
