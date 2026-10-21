namespace Sankore.Modules.Leads.Features.WhatsApp.Bot;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// Deployment settings of the WhatsApp bot's model (section <c>Leads:WhatsAppBot</c>).
///
/// <para>
/// <b>Per deployment, not per tenant</b>, like the biometry service: the model is a platform
/// supplier and its key a platform secret. A tenant decides whether ITS bot speaks
/// (<c>whatsapp-bot-enabled</c>, then per product); it does not bring a model.
/// </para>
/// </summary>
public sealed class WhatsAppBotOptions
{
    public const string Section = "Leads:WhatsAppBot";

    /// <summary><c>Anthropic</c> (default), <c>Fake</c> (Development: a scripted model) or <c>None</c>.</summary>
    public string Provider { get; set; } = "Anthropic";

    /// <summary>The Anthropic API key. Unset = no model: every conversation goes straight to a person.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-sonnet-5-5";

    public string BaseUrl { get; set; } = "https://api.anthropic.com/";

    /// <summary>The whole turn — every model call and tool — must finish within this (WA-BE-11: 15 s).</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>How many of the latest messages the model sees.</summary>
    public int MaxHistoryMessages { get; set; } = 12;

    public int MaxOutputTokens { get; set; } = 1024;
}

// ── The conversation shape a model reads and writes ──────────────────────────────────────────

internal abstract record BotBlock;

internal sealed record BotTextBlock(string Text) : BotBlock;

internal sealed record BotToolUseBlock(string Id, string Name, JsonElement Input) : BotBlock;

internal sealed record BotToolResultBlock(string ToolUseId, string Content, bool IsError) : BotBlock;

/// <param name="Role"><c>user</c> (the customer, and tool results) or <c>assistant</c>.</param>
internal sealed record BotMessage(string Role, IReadOnlyList<BotBlock> Content);

internal sealed record BotTool(string Name, string Description, JsonObject InputSchema);

internal sealed record BotModelRequest(string System, IReadOnlyList<BotMessage> Messages, IReadOnlyList<BotTool> Tools);

internal sealed record BotModelResponse(IReadOnlyList<BotBlock> Content, string? StopReason, int? InputTokens, int? OutputTokens);

/// <summary>The model could not be asked, or its answer could not be read. A code, never a body.</summary>
internal sealed class BotModelException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The language model behind the bot (WA-BE-11). A port, so the engine, its rules and its tests
/// never depend on one vendor's wire format — and so a deployment without a key has a model that
/// says so instead of one that fails on every message.
/// </summary>
internal interface IWhatsAppBotModel
{
    /// <summary>False = no bot anywhere on this deployment; every conversation escalates directly.</summary>
    bool IsAvailable { get; }

    /// <summary>What a turn records as its model.</summary>
    string Name { get; }

    string Provider { get; }

    /// <summary>One call. Tools are always required: every answer is a tool call (see the engine).</summary>
    Task<BotModelResponse> CompleteAsync(BotModelRequest request, CancellationToken ct);
}

internal sealed class UnavailableWhatsAppBotModel : IWhatsAppBotModel
{
    public bool IsAvailable => false;
    public string Name => "none";
    public string Provider => "None";

    public Task<BotModelResponse> CompleteAsync(BotModelRequest request, CancellationToken ct) =>
        throw new BotModelException("BOT_MODEL_UNAVAILABLE", "No model is configured on this deployment.");
}

/// <summary>
/// Claude through Anthropic's Messages API, with tool use. Plain HTTP rather than an SDK: one
/// endpoint and four block types, and the request is built here so what leaves the platform is
/// readable in one place.
/// </summary>
internal sealed class AnthropicWhatsAppBotModel(HttpClient http, IOptions<WhatsAppBotOptions> options) : IWhatsAppBotModel
{
    public const string HttpClientName = "WhatsAppBotModel";

    private readonly WhatsAppBotOptions _options = options.Value;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string Name => _options.Model;
    public string Provider => "Anthropic";

    public async Task<BotModelResponse> CompleteAsync(BotModelRequest request, CancellationToken ct)
    {
        if (!IsAvailable) throw new BotModelException("BOT_MODEL_UNAVAILABLE", "No Anthropic API key is configured.");

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxOutputTokens,
            ["system"] = request.System,
            ["messages"] = new JsonArray(request.Messages.Select(ToJson).ToArray<JsonNode?>()),
            ["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone(),
            }).ToArray()),
            // Every answer is a tool call — send_reply or escalate_to_human end the turn — so the
            // outcome is structured, never free text the engine would have to interpret.
            ["tool_choice"] = new JsonObject { ["type"] = "any" },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), "v1/messages"))
        {
            Content = JsonContent.Create(body),
        };

        message.Headers.Add("x-api-key", _options.ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BotModelException("BOT_MODEL_UNREACHABLE", ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Anthropic's own error type and message — « credit balance too low », « model not
                // found », « messages: … » — without which an operator cannot tell an account
                // problem from a malformed request. Never the raw body, and cut: what is kept is
                // the API's sentence about the request, not the request.
                var (type, detail) = await ReadErrorAsync(response, ct);
                throw new BotModelException(
                    $"BOT_MODEL_HTTP_{(int)response.StatusCode}",
                    $"The model answered {(int)response.StatusCode} {type}: {detail}");
            }

            JsonDocument document;

            try
            {
                document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (JsonException)
            {
                throw new BotModelException("BOT_MODEL_UNREADABLE", "The model's answer is not JSON.");
            }

            using (document)
            {
                return Parse(document.RootElement);
            }
        }
    }

    /// <summary><c>{"type":"error","error":{"type":"…","message":"…"}}</c> → its two fields, the message cut to 300 characters.</summary>
    internal static async Task<(string Type, string Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var type = error.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
                var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
                return (type, message.Length <= 300 ? message : message[..300]);
            }
        }
        catch (JsonException)
        {
            // Not Anthropic's error shape — a proxy page, an empty body.
        }

        return ("unknown", string.Empty);
    }

    internal static BotModelResponse Parse(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new BotModelException("BOT_MODEL_UNREADABLE", "The model's answer has no content.");

        var blocks = new List<BotBlock>();

        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "text":
                    blocks.Add(new BotTextBlock(block.GetProperty("text").GetString() ?? string.Empty));
                    break;
                case "tool_use":
                    blocks.Add(new BotToolUseBlock(
                        block.GetProperty("id").GetString() ?? string.Empty,
                        block.GetProperty("name").GetString() ?? string.Empty,
                        block.GetProperty("input").Clone()));
                    break;
            }
        }

        int? Usage(string name) =>
            root.TryGetProperty("usage", out var usage) && usage.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

        return new BotModelResponse(
            blocks,
            root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null,
            Usage("input_tokens"),
            Usage("output_tokens"));
    }

    private static JsonNode ToJson(BotMessage message) => new JsonObject
    {
        ["role"] = message.Role,
        ["content"] = new JsonArray(message.Content.Select(b => (JsonNode?)(b switch
        {
            BotTextBlock text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
            BotToolUseBlock use => new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = use.Id,
                ["name"] = use.Name,
                ["input"] = JsonNode.Parse(use.Input.GetRawText()),
            },
            BotToolResultBlock result => new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = result.ToolUseId,
                ["content"] = result.Content,
                ["is_error"] = result.IsError,
            },
            _ => throw new InvalidOperationException($"Unknown block {b.GetType().Name}."),
        })).ToArray()),
    };
}

/// <summary>
/// A scripted model for Development and tests — no key, no network. It reads the product's FAQ
/// through <c>get_product_info</c> like the real one would, answers when a question shares words
/// with an FAQ entry, and says it could not otherwise, so every path of the engine — the tool
/// loop, a useful answer, the « no useful answer » counter — can be walked on a laptop.
/// </summary>
internal sealed class FakeWhatsAppBotModel : IWhatsAppBotModel
{
    public bool IsAvailable => true;
    public string Name => "fake";
    public string Provider => "Fake";

    public Task<BotModelResponse> CompleteAsync(BotModelRequest request, CancellationToken ct)
    {
        var last = request.Messages[^1];
        var result = last.Content.OfType<BotToolResultBlock>().FirstOrDefault();

        if (result is null)
            return Task.FromResult(Use(WhatsAppBotTools.GetProductInfo, new JsonObject()));

        var question = request.Messages
            .Where(m => m.Role == "user")
            .SelectMany(m => m.Content.OfType<BotTextBlock>())
            .LastOrDefault()?.Text ?? string.Empty;

        var words = WhatsAppBotText.Normalize(question).Split(' ').Where(w => w.Length >= 4).ToHashSet();

        string? answer = null;

        try
        {
            using var doc = JsonDocument.Parse(result.Content);

            if (doc.RootElement.TryGetProperty("faq", out var faq))
            {
                answer = faq.EnumerateArray()
                    .Select(e => (
                        Answer: e.GetProperty("answer").GetString(),
                        Score: WhatsAppBotText.Normalize(e.GetProperty("question").GetString()).Split(' ').Count(words.Contains)))
                    .Where(e => e.Score > 0)
                    .OrderByDescending(e => e.Score)
                    .Select(e => e.Answer)
                    .FirstOrDefault();
            }
        }
        catch (JsonException)
        {
            // Not a product-info result: acknowledge whatever the previous tool was.
            answer = "C’est noté, merci.";
        }

        return Task.FromResult(Use(WhatsAppBotTools.SendReply, new JsonObject
        {
            ["text"] = answer ?? "Je n’ai pas trouvé cette information. Pouvez-vous préciser votre question ?",
            ["answered_question"] = answer is not null,
        }));
    }

    private static BotModelResponse Use(string tool, JsonObject input) =>
        new([new BotToolUseBlock($"fake_{Guid.NewGuid():N}", tool, JsonSerializer.SerializeToElement(input))], "tool_use", null, null);
}
