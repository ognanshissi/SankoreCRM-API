namespace Sankore.Modules.Integration.Adapters.Dialog360;

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Messaging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// 360dialog — the WhatsApp Business Solution Provider of the Partner programme (WA-BE-01).
///
/// <para>
/// <b>What it talks to.</b> 360dialog's v2 hosting proxies Meta's Cloud API: the message bodies,
/// the statuses, the webhook payloads and the error objects are Meta's own, and only the
/// authentication (one <c>D360-API-KEY</c> per number) and a handful of configuration routes are
/// 360dialog's. That is why the webhook reader and the error table live in the module
/// (<see cref="CloudApiWebhook"/>, <see cref="CloudApiErrors"/>) and not here: a direct Meta
/// adapter would reuse both and differ only in this class.
/// </para>
///
/// <para>
/// <b>What is NOT verified against a live account</b> is listed in
/// <see cref="Dialog360Specification.OpenQuestions"/> — notably whether the webhook's custom
/// headers are honoured on every delivery, which is what the endpoint authenticates with. The
/// integration suite of the Definition of Done needs the 360dialog sandbox and is not part of
/// this lot.
/// </para>
///
/// <para>
/// <b>Registered as <c>ICbsAdapter</c></b>, like every adapter of this module whatever its family:
/// <c>IntegrationAdapterResolver</c> resolves a keyed <c>ICbsAdapter</c> by kind and casts to the
/// port it wants.
/// </para>
/// </summary>
internal sealed class Dialog360Adapter(
    Dialog360Transport transport,
    ISecretsModule secrets,
    TimeProvider clock,
    ILogger<Dialog360Adapter> logger)
    : ICbsAdapter, IConversationalChannelPort
{
    /// <summary>The platform's ceiling on one media, in and out (spec, « Multi-tenant et sécurité »).</summary>
    internal const int MaxMediaBytes = 10 * 1024 * 1024;

    /// <summary>One page of the template catalogue. 360dialog's own maximum.</summary>
    internal const int TemplatePageSize = 1000;

    public IntegrationKind Kind => IntegrationKind.Dialog360;

    /// <summary>
    /// Every messaging capability, live. A number connected through 360dialog can do all five;
    /// what varies is whether it is ALLOWED to right now (the window, the template's approval),
    /// and those are answers to a call, not capabilities.
    /// </summary>
    public IntegrationCapabilities CapabilitiesFor(IntegrationConnection connection)
        => IntegrationCapabilities.All(
            CapabilityMode.RealTime,
            IntegrationCapability.TemplateMessages,
            IntegrationCapability.FreeformMessages,
            IntegrationCapability.MediaMessages,
            IntegrationCapability.MessageEvents,
            IntegrationCapability.TemplateCatalog);

    /// <summary>
    /// Proves the stored key works by reading one template. It reads rather than writes: a check
    /// must never send a message, and the template catalogue is the cheapest authenticated route.
    /// </summary>
    public async Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var started = clock.GetTimestamp();
        var binding = await BindAsync(connection, ct);

        if (binding.IsFailure)
            return IntegrationHealth.Unhealthy($"{binding.Code}: {binding.Detail}", clock.GetUtcNow());

        var probe = await transport.SendAsync(
            binding.Value,
            Dialog360Operations.CheckHealth,
            HttpMethod.Get,
            new Uri(binding.Value.BaseUrl, "v1/configs/templates?limit=1"),
            () => null,
            ct);

        if (probe.IsFailure)
            return IntegrationHealth.Unhealthy($"{probe.Code}: {probe.Detail}", clock.GetUtcNow());

        return IntegrationHealth.Healthy(clock.GetElapsedTime(started), clock.GetUtcNow());
    }

    // ── IConversationalChannelPort ──────────────────────────────────────────────────────────

    public async Task<IntegrationResult<string>> SendTemplateAsync(
        IntegrationConnection connection, OutboundTemplate template, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);

        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = Digits(template.ToE164),
            ["type"] = "template",
            ["template"] = new Dictionary<string, object?>
            {
                ["name"] = template.Name,
                ["language"] = new Dictionary<string, object?> { ["code"] = template.Language },
                ["components"] = template.BodyParameters.Count == 0
                    ? Array.Empty<object>()
                    : new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "body",
                            ["parameters"] = template.BodyParameters
                                .Select(p => new Dictionary<string, object?> { ["type"] = "text", ["text"] = p })
                                .ToArray(),
                        },
                    },
            },
        };

        return await SendMessageAsync(connection, Dialog360Operations.SendTemplate, body, ct);
    }

    public Task<IntegrationResult<string>> SendTextAsync(
        IntegrationConnection connection, string toE164, string text, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = Digits(toE164),
            ["type"] = "text",
            ["text"] = new Dictionary<string, object?> { ["body"] = text, ["preview_url"] = false },
        };

        return SendMessageAsync(connection, Dialog360Operations.SendText, body, ct);
    }

    /// <summary>
    /// Uploads, then sends by the media id the upload answered. Two calls and not one because the
    /// Cloud API accepts a link or an id, and a link would have to be a public URL to a customer's
    /// document.
    /// </summary>
    public async Task<IntegrationResult<SentMedia>> SendMediaAsync(
        IntegrationConnection connection, string toE164, OutboundMedia media, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (media.Content.Length is 0 or > MaxMediaBytes)
        {
            return IntegrationResult.Functional<SentMedia>(
                IntegrationErrors.MessagingMediaRefused,
                $"A media must weigh between 1 byte and {MaxMediaBytes / (1024 * 1024)} MB.");
        }

        var binding = await BindAsync(connection, ct);
        if (binding.IsFailure) return Dialog360Results.Forward<SentMedia>(binding);

        var upload = await transport.SendAsync(
            binding.Value,
            Dialog360Operations.UploadMedia,
            HttpMethod.Post,
            new Uri(binding.Value.BaseUrl, "media"),
            () =>
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent("whatsapp"), "messaging_product" },
                };

                var file = new ByteArrayContent(media.Content);
                file.Headers.ContentType = new MediaTypeHeaderValue(media.ContentType);
                form.Add(file, "file", media.FileName);

                return form;
            },
            ct);

        if (upload.IsFailure) return Dialog360Results.Forward<SentMedia>(upload);

        var mediaId = ReadString(upload.Value, "id");

        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return IntegrationResult.Functional<SentMedia>(
                IntegrationErrors.UnexpectedResponse,
                $"{Dialog360Operations.UploadMedia}: the answer carries no media id.");
        }

        var kind = media.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? "image"
            : "document";

        var payload = new Dictionary<string, object?> { ["id"] = mediaId };
        if (!string.IsNullOrWhiteSpace(media.Caption)) payload["caption"] = media.Caption;
        if (kind == "document") payload["filename"] = media.FileName;

        var body = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = Digits(toE164),
            ["type"] = kind,
            [kind] = payload,
        };

        var sent = await SendMessageAsync(connection, Dialog360Operations.SendMedia, body, ct, binding.Value);

        return sent.IsSuccess
            ? IntegrationResult.Ok(new SentMedia(sent.Value, mediaId))
            : Dialog360Results.Forward<SentMedia>(sent);
    }

    public async Task<IntegrationResult<IReadOnlyList<ProviderTemplate>>> ListTemplatesAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        var binding = await BindAsync(connection, ct);
        if (binding.IsFailure) return Dialog360Results.Forward<IReadOnlyList<ProviderTemplate>>(binding);

        var templates = new List<ProviderTemplate>();

        // Paged, though one page holds a thousand: an institution with more templates than that
        // has a catalogue problem of its own, and the loop is bounded so a provider repeating the
        // same page cannot hold this job forever.
        for (var page = 0; page < 10; page++)
        {
            var offset = page * TemplatePageSize;

            var answer = await transport.SendAsync(
                binding.Value,
                Dialog360Operations.ListTemplates,
                HttpMethod.Get,
                new Uri(
                    binding.Value.BaseUrl,
                    $"v1/configs/templates?limit={TemplatePageSize}&offset={offset.ToString(CultureInfo.InvariantCulture)}"),
                () => null,
                ct);

            if (answer.IsFailure) return Dialog360Results.Forward<IReadOnlyList<ProviderTemplate>>(answer);

            var (read, total) = Dialog360Templates.Read(answer.Value);

            if (read is null)
            {
                return IntegrationResult.Functional<IReadOnlyList<ProviderTemplate>>(
                    IntegrationErrors.UnexpectedResponse,
                    $"{Dialog360Operations.ListTemplates}: the answer carries no waba_templates array.");
            }

            templates.AddRange(read);

            if (read.Count < TemplatePageSize || total is { } t && templates.Count >= t) break;
        }

        return IntegrationResult.Ok<IReadOnlyList<ProviderTemplate>>(templates);
    }

    /// <summary>
    /// Two calls: the media's metadata, then its bytes. The bytes' URL points at Meta's CDN
    /// (<c>lookaside.fbsbx.com</c>), which 360dialog documents as reachable through its own host
    /// with the same API key — the host is swapped and the path kept.
    /// </summary>
    /// <summary>
    /// <c>DELETE {base}/{media-id}</c>, the Cloud API's own media deletion, through 360dialog's
    /// proxy — see <see cref="Dialog360Specification.OpenQuestions"/> point 5. A 404 is a success:
    /// inbound media expire at Meta on their own after thirty days.
    /// </summary>
    public async Task<IntegrationResult> DeleteMediaAsync(
        IntegrationConnection connection, string mediaId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || !mediaId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            return IntegrationResult.Functional(IntegrationErrors.PayloadInvalid, "The media id is not an identifier.");

        var binding = await BindAsync(connection, ct);
        if (binding.IsFailure) return binding;

        var deleted = await transport.SendAsync(
            binding.Value,
            Dialog360Operations.DeleteMedia,
            HttpMethod.Delete,
            new Uri(binding.Value.BaseUrl, Uri.EscapeDataString(mediaId)),
            () => null,
            ct);

        // The status is only carried in the detail the classification writes: « answered HTTP 404 ».
        if (deleted.IsSuccess || deleted.Detail?.Contains("answered HTTP 404", StringComparison.Ordinal) == true)
            return IntegrationResult.Ok();

        return deleted;
    }

    public async Task<IntegrationResult<ProviderMedia>> DownloadMediaAsync(
        IntegrationConnection connection, string mediaId, CancellationToken ct)
    {
        // The id is a path segment of a request carrying an API key: anything but an identifier
        // would let a caller steer that request.
        if (string.IsNullOrWhiteSpace(mediaId) || !mediaId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return IntegrationResult.Functional<ProviderMedia>(
                IntegrationErrors.PayloadInvalid, "The media id is not an identifier.");
        }

        var binding = await BindAsync(connection, ct);
        if (binding.IsFailure) return Dialog360Results.Forward<ProviderMedia>(binding);

        var meta = await transport.SendAsync(
            binding.Value,
            Dialog360Operations.GetMedia,
            HttpMethod.Get,
            new Uri(binding.Value.BaseUrl, Uri.EscapeDataString(mediaId)),
            () => null,
            ct);

        if (meta.IsFailure) return Dialog360Results.Forward<ProviderMedia>(meta);

        var url = ReadString(meta.Value, "url");
        var mime = ReadString(meta.Value, "mime_type");
        var size = ReadLong(meta.Value, "file_size");

        if (size is > MaxMediaBytes)
        {
            return IntegrationResult.Functional<ProviderMedia>(
                IntegrationErrors.MessagingMediaRefused,
                $"The media weighs more than {MaxMediaBytes / (1024 * 1024)} MB.");
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var cdn))
        {
            return IntegrationResult.Functional<ProviderMedia>(
                IntegrationErrors.UnexpectedResponse,
                $"{Dialog360Operations.GetMedia}: the answer carries no download URL.");
        }

        // Only the path and the query are taken from the answer: the HOST is always ours to
        // choose, so a provider answer can never send our API key somewhere else.
        var download = new Uri(binding.Value.BaseUrl, cdn.PathAndQuery.TrimStart('/'));

        var bytes = await transport.SendForBytesAsync(binding.Value, Dialog360Operations.DownloadMedia, download, ct);

        if (bytes.IsFailure) return Dialog360Results.Forward<ProviderMedia>(bytes);

        if (bytes.Value.Content.Length > MaxMediaBytes)
        {
            return IntegrationResult.Functional<ProviderMedia>(
                IntegrationErrors.MessagingMediaRefused,
                $"The media weighs more than {MaxMediaBytes / (1024 * 1024)} MB.");
        }

        return IntegrationResult.Ok(new ProviderMedia(
            bytes.Value.Content,
            string.IsNullOrWhiteSpace(mime) ? bytes.Value.ContentType : mime,
            bytes.Value.FileName));
    }

    public async Task<IntegrationResult> ConfigureWebhookAsync(
        IntegrationConnection connection,
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(headers);

        var binding = await BindAsync(connection, ct);
        if (binding.IsFailure) return Dialog360Results.Forward(binding);

        var answer = await transport.SendAsync(
            binding.Value,
            Dialog360Operations.ConfigureWebhook,
            HttpMethod.Post,
            new Uri(binding.Value.BaseUrl, "v1/configs/webhook"),
            () => Dialog360Transport.Json(new Dictionary<string, object?>
            {
                ["url"] = url.AbsoluteUri,
                ["headers"] = headers,
            }),
            ct);

        return answer.IsSuccess ? IntegrationResult.Ok() : Dialog360Results.Forward(answer);
    }

    public ConversationalWebhookBatch ParseWebhook(string body) => CloudApiWebhook.Parse(body);

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task<IntegrationResult<string>> SendMessageAsync(
        IntegrationConnection connection,
        string operation,
        Dictionary<string, object?> body,
        CancellationToken ct,
        Dialog360Binding? bound = null)
    {
        Dialog360Binding binding;

        if (bound is not null)
        {
            binding = bound;
        }
        else
        {
            var resolved = await BindAsync(connection, ct);
            if (resolved.IsFailure) return Dialog360Results.Forward<string>(resolved);
            binding = resolved.Value;
        }

        var answer = await transport.SendAsync(
            binding, operation, HttpMethod.Post, new Uri(binding.BaseUrl, "messages"),
            () => Dialog360Transport.Json(body), ct);

        if (answer.IsFailure) return answer;

        var id = Dialog360Messages.ReadMessageId(answer.Value);

        if (string.IsNullOrWhiteSpace(id))
        {
            // Accepted with no id is the one answer that cannot be followed: no status will ever
            // name it. Reported as an unexpected answer rather than a success, so the agent sees
            // the message as uncertain instead of watching it sit at « envoyé » for ever.
            logger.LogWarning(
                "360dialog accepted {Operation} on connection {ConnectionId} without a message id.",
                operation, connection.Id);

            return IntegrationResult.Functional<string>(
                IntegrationErrors.UnexpectedResponse, $"{operation}: the answer carries no message id.");
        }

        return IntegrationResult.Ok(id);
    }

    /// <summary>
    /// The connection's coordinates and key, read per call. The VALUE of the key is read here and
    /// nowhere else, and only to be set on a request header.
    /// </summary>
    private async Task<IntegrationResult<Dialog360Binding>> BindAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Settings is not Dialog360Settings settings)
        {
            return IntegrationResult.Technical<Dialog360Binding>(
                IntegrationErrors.SettingsInvalid,
                "This connection does not carry 360dialog settings.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
            ? Dialog360Settings.DefaultApiBaseUrl
            : settings.ApiBaseUrl.Trim();

        // Re-checked here, not only at save: a row written before the host list existed must not
        // carry the key anywhere else either.
        if (!Dialog360Settings.IsAllowedApiBaseUrl(baseUrl)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root))
        {
            return IntegrationResult.Technical<Dialog360Binding>(
                IntegrationErrors.SettingsInvalid, "settings.apiBaseUrl must be a 360dialog host.");
        }

        var key = await secrets.GetValueAsync(
            IntegrationSecrets.CredentialKey(connection.TenantId, connection.Id), ct);

        if (string.IsNullOrWhiteSpace(key))
        {
            return IntegrationResult.Technical<Dialog360Binding>(
                IntegrationErrors.CredentialMissing,
                "No 360dialog API key is stored for this number. Save it on the WhatsApp settings "
                + "screen; 360dialog issues one key per number.");
        }

        return IntegrationResult.Ok(new Dialog360Binding(connection.TenantId, connection.Id, root, key.Trim()));
    }

    /// <summary>Meta wants the recipient as digits, without the plus.</summary>
    private static string Digits(string e164) => new(e164.Where(char.IsAsciiDigit).ToArray());

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadLong(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(property, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Names the call journal files 360dialog's calls under.</summary>
internal static class Dialog360Operations
{
    public const string CheckHealth = "dialog360.check-health";
    public const string SendTemplate = "dialog360.send-template";
    public const string SendText = "dialog360.send-text";
    public const string SendMedia = "dialog360.send-media";
    public const string UploadMedia = "dialog360.upload-media";
    public const string ListTemplates = "dialog360.list-templates";
    public const string GetMedia = "dialog360.get-media";
    public const string DownloadMedia = "dialog360.download-media";
    public const string DeleteMedia = "dialog360.delete-media";
    public const string ConfigureWebhook = "dialog360.configure-webhook";
}

/// <summary>Reads the answer to a send.</summary>
internal static class Dialog360Messages
{
    /// <summary>The <c>wamid</c> of <c>{"messages":[{"id":"wamid…"}]}</c>, or null.</summary>
    public static string? ReadMessageId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("messages", out var messages)
                   && messages.ValueKind == JsonValueKind.Array
                   && messages.GetArrayLength() > 0
                   && messages[0].TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Reads 360dialog's template catalogue.</summary>
internal static class Dialog360Templates
{
    /// <summary>
    /// The templates of one page and the catalogue's total, or a null list when the answer is not
    /// the shape 360dialog documents (<c>{"waba_templates":[…],"total":…}</c>).
    /// </summary>
    public static (IReadOnlyList<ProviderTemplate>? Templates, int? Total) Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("waba_templates", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            int? total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : null;
            var templates = new List<ProviderTemplate>();

            foreach (var item in items.EnumerateArray())
            {
                var name = Text(item, "name");
                var language = Text(item, "language");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language)) continue;

                string? header = null, body = null, footer = null;
                var buttons = new List<string>();

                if (item.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
                {
                    foreach (var component in components.EnumerateArray())
                    {
                        switch (Text(component, "type")?.ToUpperInvariant())
                        {
                            case "HEADER" when string.Equals(Text(component, "format"), "TEXT", StringComparison.OrdinalIgnoreCase)
                                               || Text(component, "format") is null:
                                header = Text(component, "text");
                                break;
                            case "BODY":
                                body = Text(component, "text");
                                break;
                            case "FOOTER":
                                footer = Text(component, "text");
                                break;
                            case "BUTTONS" when component.TryGetProperty("buttons", out var list)
                                                && list.ValueKind == JsonValueKind.Array:
                                buttons.AddRange(list.EnumerateArray()
                                    .Select(b => Text(b, "text"))
                                    .Where(b => !string.IsNullOrWhiteSpace(b))
                                    .Select(b => b!));
                                break;
                        }
                    }
                }

                templates.Add(new ProviderTemplate(
                    ProviderTemplateId: Text(item, "id"),
                    Name: name,
                    Language: language,
                    Category: (Text(item, "category") ?? "UNKNOWN").ToUpperInvariant(),
                    Status: (Text(item, "status") ?? "UNKNOWN").ToUpperInvariant(),
                    HeaderText: header,
                    BodyText: body ?? string.Empty,
                    FooterText: footer,
                    Buttons: buttons));
            }

            return (templates, total);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
