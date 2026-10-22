namespace Sankore.Modules.Leads.Domain;

using System.Text.RegularExpressions;
using Sankore.Shared.Kernel;

/// <summary>
/// One saved version of the WhatsApp bot's settings for a product line (WA-BE-12) — or, with no
/// product line, the GENERAL profile that answers a lead whose product nobody resolved yet.
///
/// <para>
/// <b>Immutable, one row per save.</b> A save writes version n+1 and never touches n: each
/// <see cref="WhatsAppBotTurn"/> records the version it ran under, and a turn read next year must
/// be explained by the instructions the bot actually had, not by today's. The current settings
/// are the highest version; « désactiver » is a save like any other, so the history says who
/// switched the bot off and when.
/// </para>
///
/// <para>
/// <b>The general profile exists because the main door has no product.</b> A number nobody knows
/// becomes a lead whose product is <c>"Unknown"</c> — it resolves to no line by design — and a bot
/// configurable only per product would never answer the very prospects the channel is for.
/// </para>
/// </summary>
public sealed class WhatsAppBotProfile : ITenant
{
    public const int InstructionsMaxLength = 4000;
    public const int MessageMaxLength = 1000;
    public const int FaqMaxEntries = 40;
    public const int FaqTextMaxLength = 1000;
    public const int KeywordsMaxEntries = 40;
    public const string DefaultTimeZone = "Africa/Abidjan";

    /// <summary>« montant, taux, réclamation » — the specification's own examples, plus « plainte ».</summary>
    public static readonly IReadOnlyList<string> DefaultKeywords = ["montant", "taux", "réclamation", "plainte"];

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// The product line (a <c>ProductSpeciality</c> id, opaque). <see cref="Guid.Empty"/> is the
    /// general profile — a sentinel and not null, because PostgreSQL treats every null as distinct
    /// and the unique (tenant, line, version) index would then let two « version 3 » of the
    /// general profile coexist. The API turns it back into null.
    /// </summary>
    public Guid ProductLineId { get; private set; }

    public bool IsGeneral => ProductLineId == Guid.Empty;

    /// <summary>1, 2, 3… per (tenant, line). Version 0 is the unsaved default and is never stored.</summary>
    public int Version { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>Sent once when the bot opens a conversation. Empty sends nothing.</summary>
    public string? WelcomeMessage { get; private set; }

    /// <summary>
    /// Sent instead of the tenant's waiting message when the bot hands over outside the agents'
    /// hours. Empty falls back to the waiting message.
    /// </summary>
    public string? OutOfHoursMessage { get; private set; }

    /// <summary>What the institution tells the bot about this product — tone, scope, what not to say.</summary>
    public string? Instructions { get; private set; }

    public List<WhatsAppBotFaqEntry> Faq { get; private set; } = [];

    /// <summary>Codes of <see cref="WhatsAppBotFields"/> the bot may write on the lead.</summary>
    public List<string> FieldsToCollect { get; private set; } = [];

    /// <summary>
    /// The minimum (a subset of <see cref="FieldsToCollect"/>) whose completion creates the lead of
    /// an unknown number, then hands the conversation to a person. Empty = the lead is created only
    /// at a handover, and a conversation completing nothing never hands over on that ground.
    /// </summary>
    public List<string> FieldsRequiredForLead { get; private set; } = [];

    /// <summary>When a person can take over. Empty = no hours declared, never « out of hours ».</summary>
    public List<WhatsAppBotOpeningSlot> AgentHours { get; private set; } = [];

    /// <summary>IANA zone the hours are written in. The tenant has none of its own.</summary>
    public string TimeZoneId { get; private set; } = DefaultTimeZone;

    // ── Escalation rules (WA-BE-13) ─────────────────────────────────────

    public List<string> EscalationKeywords { get; private set; } = [];

    /// <summary>Consecutive turns the bot may fail to answer before it hands over. 0 = never on this ground.</summary>
    public int MaxTurnsWithoutAnswer { get; private set; }

    /// <summary>
    /// Minutes before the 24-hour window closes at which a conversation still with the bot goes to
    /// a person, who can still answer freely. 0 = never on this ground.
    /// </summary>
    public int WindowExpiryThresholdMinutes { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private WhatsAppBotProfile() { } // EF Core

    /// <summary>What an unsaved line reads as: switched off, with the specification's defaults.</summary>
    public static WhatsAppBotProfile Default(Guid tenantId, Guid productLineId) => new()
    {
        Id = Guid.Empty,
        TenantId = tenantId,
        ProductLineId = productLineId,
        Version = 0,
        IsEnabled = false,
        EscalationKeywords = [.. DefaultKeywords],
        MaxTurnsWithoutAnswer = 2,
        WindowExpiryThresholdMinutes = 120,
        TimeZoneId = DefaultTimeZone,
    };

    public static Result<WhatsAppBotProfile> Create(
        Guid tenantId,
        Guid productLineId,
        int version,
        bool isEnabled,
        string? welcomeMessage,
        string? outOfHoursMessage,
        string? instructions,
        IReadOnlyList<WhatsAppBotFaqEntry>? faq,
        IReadOnlyList<string>? fieldsToCollect,
        IReadOnlyList<WhatsAppBotOpeningSlot>? agentHours,
        string? timeZoneId,
        IReadOnlyList<string>? escalationKeywords,
        int maxTurnsWithoutAnswer,
        int windowExpiryThresholdMinutes,
        Guid createdBy,
        DateTimeOffset now,
        IReadOnlyList<string>? fieldsRequiredForLead = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (version < 1) throw new DomainException("A saved version starts at 1.");

        if (Clip(instructions, int.MaxValue) is { Length: > InstructionsMaxLength })
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_INSTRUCTIONS_TOO_LONG");

        if (Clip(welcomeMessage, int.MaxValue) is { Length: > MessageMaxLength }
            || Clip(outOfHoursMessage, int.MaxValue) is { Length: > MessageMaxLength })
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_MESSAGE_TOO_LONG");

        var entries = (faq ?? [])
            .Select(e => new WhatsAppBotFaqEntry(Clip(e.Question, int.MaxValue) ?? "", Clip(e.Answer, int.MaxValue) ?? ""))
            .Where(e => e.Question.Length > 0 || e.Answer.Length > 0)
            .ToList();

        if (entries.Count > FaqMaxEntries) return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_FAQ_TOO_LONG");
        if (entries.Any(e => e.Question.Length == 0 || e.Answer.Length == 0))
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_FAQ_INCOMPLETE");
        if (entries.Any(e => e.Question.Length > FaqTextMaxLength || e.Answer.Length > FaqTextMaxLength))
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_FAQ_TOO_LONG");

        var fields = (fieldsToCollect ?? []).Select(WhatsAppBotFields.Normalize).Distinct().ToList();
        if (fields.Any(f => !WhatsAppBotFields.IsKnown(f)))
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_FIELD_UNKNOWN");

        var required = (fieldsRequiredForLead ?? []).Select(WhatsAppBotFields.Normalize).Distinct().ToList();
        if (required.Any(f => !fields.Contains(f)))
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_REQUIRED_NOT_COLLECTED");

        var slots = (agentHours ?? []).ToList();
        if (slots.Any(s => !s.IsValid)) return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_HOURS_INVALID");

        var zone = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZone : timeZoneId.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_TIMEZONE_UNKNOWN");

        var keywords = (escalationKeywords ?? [])
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .DistinctBy(WhatsAppBotText.Normalize)
            .ToList();

        if (keywords.Count > KeywordsMaxEntries || keywords.Any(k => k.Length > 60))
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_KEYWORDS_TOO_LONG");

        if (maxTurnsWithoutAnswer is < 0 or > 10)
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_MAX_TURNS_INVALID");

        if (windowExpiryThresholdMinutes is < 0 or > 23 * 60)
            return Result.Fail<WhatsAppBotProfile>("WHATSAPP_BOT_WINDOW_THRESHOLD_INVALID");

        return Result.Ok(new WhatsAppBotProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProductLineId = productLineId,
            Version = version,
            IsEnabled = isEnabled,
            WelcomeMessage = Clip(welcomeMessage, MessageMaxLength),
            OutOfHoursMessage = Clip(outOfHoursMessage, MessageMaxLength),
            Instructions = Clip(instructions, InstructionsMaxLength),
            Faq = entries,
            FieldsToCollect = fields,
            FieldsRequiredForLead = required,
            AgentHours = slots,
            TimeZoneId = zone,
            EscalationKeywords = keywords,
            MaxTurnsWithoutAnswer = maxTurnsWithoutAnswer,
            WindowExpiryThresholdMinutes = windowExpiryThresholdMinutes,
            CreatedBy = createdBy,
            CreatedAt = now,
        });
    }

    /// <summary>
    /// Whether a person is expected to be at work at <paramref name="now"/>. No hours declared
    /// answers true: an institution that said nothing must not have every handover called
    /// « out of hours ».
    /// </summary>
    public bool IsWithinAgentHours(DateTimeOffset now)
    {
        if (AgentHours.Count == 0) return true;

        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);

        return AgentHours.Any(s => s.Day == local.DayOfWeek && s.Contains(time));
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>One question the institution answers the same way every time.</summary>
public sealed record WhatsAppBotFaqEntry(string Question, string Answer);

/// <summary>A day and a range of hours, « HH:mm » in the profile's zone. The end is exclusive.</summary>
public sealed record WhatsAppBotOpeningSlot(DayOfWeek Day, string Start, string End)
{
    public bool IsValid =>
        TimeOnly.TryParseExact(Start, "HH:mm", out var s)
        && TimeOnly.TryParseExact(End, "HH:mm", out var e)
        && e > s;

    public bool Contains(TimeOnly time) =>
        TimeOnly.TryParseExact(Start, "HH:mm", out var s)
        && TimeOnly.TryParseExact(End, "HH:mm", out var e)
        && time >= s && time < e;
}

/// <summary>
/// The lead fields the bot may write (WA-BE-11 <c>update_lead_field</c>). A CLOSED list, like the
/// template variables: a model asked to fill a field cannot name one this module would have to
/// resolve dynamically, and none of them is an identity document — the bot never asks for one.
/// </summary>
public static class WhatsAppBotFields
{
    public const string FirstName = "FIRST_NAME";
    public const string LastName = "LAST_NAME";
    public const string Gender = "GENDER";
    public const string Email = "EMAIL";
    public const string City = "CITY";
    public const string Neighbourhood = "NEIGHBOURHOOD";
    public const string Product = "PRODUCT";
    public const string DesiredAmount = "DESIRED_AMOUNT";

    /// <summary>Code → French label, in the order the settings screen lists them.</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> All =
    [
        (FirstName, "Prénom"),
        (LastName, "Nom"),
        (Gender, "Genre"),
        (Email, "Adresse e-mail"),
        (City, "Ville"),
        (Neighbourhood, "Quartier"),
        (Product, "Produit souhaité"),
        (DesiredAmount, "Montant souhaité"),
    ];

    /// <summary>
    /// A custom field of the tenant's lead form (dynamic forms), as <c>custom.&lt;key&gt;</c> — the
    /// prefix the imports and the ingestion mappings already use. Its key is the form's, kept as
    /// written: custom keys are case-sensitive. Whether the key exists is checked against the form
    /// when the profile is saved; at a turn, a field the form no longer has is simply not offered.
    /// </summary>
    public const string CustomPrefix = "custom.";

    public static bool IsCustom(string code) => code.StartsWith(CustomPrefix, StringComparison.Ordinal);

    public static string CustomKey(string code) => code[CustomPrefix.Length..];

    /// <summary>Native codes upper-cased, a custom code's prefix lower-cased and its key untouched.</summary>
    public static string Normalize(string code)
    {
        var trimmed = code.Trim();
        return trimmed.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase)
            ? CustomPrefix + trimmed[CustomPrefix.Length..].Trim()
            : trimmed.ToUpperInvariant();
    }

    public static bool IsKnown(string code) =>
        All.Any(f => f.Code == code) || (IsCustom(code) && CustomKey(code).Length > 0);

    /// <summary>The French label of a native code; a custom code's label is its form's, resolved by the caller.</summary>
    public static string LabelOf(string code) =>
        All.FirstOrDefault(f => f.Code == code).Label ?? (IsCustom(code) ? CustomKey(code) : code);
}

/// <summary>What one bot turn ended with.</summary>
public enum WhatsAppBotTurnOutcome
{
    /// <summary>The bot answered.</summary>
    Replied,

    /// <summary>The model chose to hand over (<c>escalate_to_human</c>).</summary>
    Escalated,

    /// <summary>A deterministic rule handed over before any model was asked.</summary>
    RuleEscalated,

    /// <summary>The model failed or ran out of time: waiting message and handover.</summary>
    Failed,

    /// <summary>The answer came back when it was no longer wanted — a person took over, or the customer wrote again.</summary>
    Discarded,
}

/// <summary>
/// One exchange between a customer's message and the bot (WA-BE-11): what was sent to the model,
/// what came back, the tools it called, how long it took and under which settings.
///
/// <para>
/// <b>Input, output and tool calls are encrypted</b> with M13's key: the input is the minimised
/// transcript and still a customer's words, and a tool call carries the value the bot wrote on
/// the lead. The rest — model, latency, tokens, outcome, version — is what an operator reads.
/// </para>
/// </summary>
public sealed class WhatsAppBotTurn : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }

    /// <summary>The customer message this turn answered. Unique: a redelivered event runs nothing twice.</summary>
    public Guid InboundMessageId { get; private set; }

    public Guid? ProductLineId { get; private set; }
    public int ProfileVersion { get; private set; }

    /// <summary>The model's name, or <c>rules</c> when a rule decided before any model was asked.</summary>
    public string Model { get; private set; } = default!;

    public string? Input { get; private set; }
    public string? Output { get; private set; }
    public string? ToolCalls { get; private set; }

    public WhatsAppBotTurnOutcome Outcome { get; private set; }
    public string? EscalationReason { get; private set; }

    /// <summary>Whether the bot declared it answered the question. Null when it did not reply.</summary>
    public bool? Answered { get; private set; }

    public int LatencyMs { get; private set; }
    public int? InputTokens { get; private set; }
    public int? OutputTokens { get; private set; }
    public string? ErrorCode { get; private set; }

    /// <summary>The bot's message, when it sent one.</summary>
    public Guid? ReplyMessageId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private WhatsAppBotTurn() { } // EF Core

    public static WhatsAppBotTurn Record(
        Guid tenantId,
        Guid conversationId,
        Guid inboundMessageId,
        Guid? productLineId,
        int profileVersion,
        string model,
        string? input,
        string? output,
        string? toolCalls,
        WhatsAppBotTurnOutcome outcome,
        string? escalationReason,
        bool? answered,
        int latencyMs,
        int? inputTokens,
        int? outputTokens,
        string? errorCode,
        Guid? replyMessageId,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ConversationId = conversationId,
        InboundMessageId = inboundMessageId,
        ProductLineId = productLineId,
        ProfileVersion = profileVersion,
        Model = model.Length <= 100 ? model : model[..100],
        Input = input,
        Output = output,
        ToolCalls = toolCalls,
        Outcome = outcome,
        EscalationReason = escalationReason,
        Answered = answered,
        LatencyMs = Math.Max(0, latencyMs),
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        ErrorCode = errorCode is { Length: > 60 } ? errorCode[..60] : errorCode,
        ReplyMessageId = replyMessageId,
        CreatedAt = now,
    };

    /// <summary>With the conversation it belongs to (BNF-B-19): the words go, the measurements stay.</summary>
    internal void Anonymize()
    {
        Input = null;
        Output = null;
        ToolCalls = null;
    }
}

/// <summary>
/// Text helpers shared by the rules and the redaction: one definition of « the same word ».
/// </summary>
public static partial class WhatsAppBotText
{
    /// <summary>Lower case, accents removed, every run of non-letters and non-digits a single space.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>Whether <paramref name="phrase"/> appears in <paramref name="normalizedText"/> as whole words.</summary>
    public static bool ContainsPhrase(string normalizedText, string phrase)
    {
        var p = Normalize(phrase);
        if (p.Length == 0 || normalizedText.Length == 0) return false;
        return $" {normalizedText} ".Contains($" {p} ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
