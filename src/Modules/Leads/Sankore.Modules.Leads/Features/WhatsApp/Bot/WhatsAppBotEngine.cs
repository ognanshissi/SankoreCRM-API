namespace Sankore.Modules.Leads.Features.WhatsApp.Bot;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel.Realtime;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Runs one bot turn per customer message the bot holds (WA-BE-11), out of the webhook's way:
/// the delivery is recorded and acknowledged first, the model is asked afterwards.
/// </summary>
public sealed class WhatsAppBotTurnConsumer(IServiceScopeFactory scopeFactory)
    : IConsumer<WhatsAppBotTurnRequestedEvent>
{
    public async Task Consume(ConsumeContext<WhatsAppBotTurnRequestedEvent> context)
    {
        var message = context.Message;
        if (message.TenantId == Guid.Empty || message.ConversationId == Guid.Empty) return;

        // The ambient scope first: ITenantContext is built from it, and a consumer has no request.
        using var bgCtx = BackgroundJobContext.SetScope(message.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        await scope.ServiceProvider.GetRequiredService<WhatsAppBotEngine>()
            .RunAsync(message.TenantId, message.ConversationId, message.InboundMessageId, context.CancellationToken);
    }
}

/// <summary>
/// One bot turn (WA-BE-11 → WA-BE-13): the deterministic rules, then the model with its four
/// tools, then the reply or the handover — and a <see cref="WhatsAppBotTurn"/> either way.
///
/// <para>
/// <b>Every answer is a tool call.</b> The model must end its turn with <c>send_reply</c> (text
/// plus « did I answer the question ») or <c>escalate_to_human</c> (a reason from a closed list
/// plus a summary). Nothing it writes outside a tool reaches the customer, so the engine never
/// has to guess whether a paragraph was meant as an answer, and the « no useful answer » rule
/// counts a declaration instead of a guess.
/// </para>
///
/// <para>
/// <b>A turn that comes back too late is dropped, not sent.</b> The model is asked outside any
/// lock; when it answers, the conversation is re-read. An agent who took over in the meantime, or a
/// customer who wrote again — whose own turn sees this message in its history — makes the answer
/// moot, and sending it would have two voices answering one customer. The fields it collected are
/// kept: the customer did give them.
/// </para>
///
/// <para>
/// <b>Failure is a handover.</b> A model that times out (15 s for the whole turn, every call
/// included), answers an error or never decides hands the conversation to a person with the
/// waiting message — a customer is never left with silence because a supplier was slow.
/// </para>
/// </summary>
internal sealed class WhatsAppBotEngine(
    LeadsDbContext db,
    WhatsAppBotProfiles profiles,
    IWhatsAppBotModel model,
    WhatsAppEscalation escalation,
    IAdministrationModule admin,
    ILeadEmailIndexer emailIndexer,
    IRealtimeNotifier realtime,
    IOptions<WhatsAppBotOptions> options,
    TimeProvider clock,
    ILogger<WhatsAppBotEngine> logger)
{
    /// <summary>Model calls in one turn: a product lookup, a field or two, then the answer.</summary>
    internal const int MaxModelCalls = 4;

    private const string RulesModel = "rules";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(Guid tenantId, Guid conversationId, Guid inboundMessageId, CancellationToken ct)
    {
        // Idempotence: a redelivered event finds its turn and does nothing.
        if (await db.WhatsAppBotTurns.AnyAsync(t => t.InboundMessageId == inboundMessageId, ct)) return;

        var conversation = await db.WhatsAppConversations.AsTracking().FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is not { Status: WhatsAppConversationStatus.Bot }) return;

        var trigger = await db.WhatsAppMessages
            .FirstOrDefaultAsync(m => m.Id == inboundMessageId && m.ConversationId == conversationId, ct);

        if (trigger is not { Direction: WhatsAppDirection.Inbound }) return;

        // Two messages in a row: the second one's turn reads both, this one stays quiet.
        if (await HasNewerInboundAsync(conversation.Id, trigger, ct)) return;

        var lead = conversation.LeadId is { } leadId
            ? await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct)
            : null;

        var now = clock.GetUtcNow();
        var profile = await profiles.ResolveActiveAsync(tenantId, lead, ct);

        if (profile is null)
        {
            // Switched off between the message and its turn: the specification's « désactivé =
            // escalade directe », applied late rather than not at all.
            await escalation.EscalateAsync(tenantId, conversation, lead, WhatsAppBotReasons.Disabled,
                "Le bot n’est plus actif pour ce prospect.", now, profile: null, tellCustomer: true, ct);

            await CommitAsync(tenantId, conversation, lead, WhatsAppBotTurn.Record(
                tenantId, conversation.Id, trigger.Id, lead?.ProductLineId, profileVersion: 0, RulesModel,
                input: null, output: null, toolCalls: null, WhatsAppBotTurnOutcome.RuleEscalated, WhatsAppBotReasons.Disabled,
                answered: null, latencyMs: 0, null, null, errorCode: null, replyMessageId: null, now), ct);
            return;
        }

        var verdict = WhatsAppBotRules.BeforeModel(
            trigger.Kind, trigger.Body, lead?.Status, conversation.BotUnansweredTurns, profile);

        if (verdict is not null)
        {
            await escalation.EscalateAsync(tenantId, conversation, lead, verdict.Reason, verdict.Summary, now, profile, tellCustomer: true, ct);

            await CommitAsync(tenantId, conversation, lead, WhatsAppBotTurn.Record(
                tenantId, conversation.Id, trigger.Id, lead?.ProductLineId, profile.Version, RulesModel,
                input: null, output: null, toolCalls: null, WhatsAppBotTurnOutcome.RuleEscalated, verdict.Reason,
                answered: null, latencyMs: 0, null, null, errorCode: null, replyMessageId: null, now), ct);
            return;
        }

        var watch = Stopwatch.StartNew();
        var run = await AskAsync(tenantId, conversation, lead, trigger, profile, ct);
        watch.Stop();

        // Re-read what may have moved while the model was thinking. Only the conversation: the
        // lead carries the fields the bot just wrote, which are kept whatever happens next.
        await db.Entry(conversation).ReloadAsync(ct);

        foreach (var code in run.Collected) conversation.RecordBotCollected(code);

        now = clock.GetUtcNow();
        var stillWanted = conversation.Status == WhatsAppConversationStatus.Bot && !await HasNewerInboundAsync(conversation.Id, trigger, ct);

        WhatsAppBotTurnOutcome outcome;
        string? reason = null;
        Guid? replyId = null;

        if (!stillWanted)
        {
            outcome = WhatsAppBotTurnOutcome.Discarded;
        }
        else if (run.Failure is not null)
        {
            outcome = WhatsAppBotTurnOutcome.Failed;
            reason = WhatsAppBotReasons.Failure;
            await escalation.EscalateAsync(tenantId, conversation, lead, reason,
                run.Failure == "BOT_TIMEOUT"
                    ? "Le bot n’a pas répondu à temps ; le client attend un conseiller."
                    : "Le bot n’a pas pu répondre ; le client attend un conseiller.",
                now, profile, tellCustomer: true, ct);
        }
        else if (run.Escalation is { } handover)
        {
            outcome = WhatsAppBotTurnOutcome.Escalated;
            reason = handover.Reason;
            await escalation.EscalateAsync(tenantId, conversation, lead, handover.Reason, handover.Summary, now, profile, tellCustomer: true, ct);
        }
        else
        {
            var reply = run.Reply!;
            var unanswered = conversation.RecordBotAnswer(reply.Answered);

            if (!reply.Answered && profile.MaxTurnsWithoutAnswer > 0 && unanswered >= profile.MaxTurnsWithoutAnswer)
            {
                // The threshold is reached by THIS turn: a person answers instead of a second
                // « je ne sais pas », rather than after the customer has read it.
                outcome = WhatsAppBotTurnOutcome.RuleEscalated;
                reason = WhatsAppBotReasons.NoUsefulAnswer;
                await escalation.EscalateAsync(tenantId, conversation, lead, reason,
                    $"Le bot n’a pas su répondre {unanswered} fois de suite.", now, profile, tellCustomer: true, ct);
            }
            else
            {
                outcome = WhatsAppBotTurnOutcome.Replied;
                replyId = (await escalation.QueueAutomaticAsync(tenantId, conversation, WhatsAppAuthor.Bot, reply.Text, now, ct)).Id;
            }
        }

        await CommitAsync(tenantId, conversation, lead, WhatsAppBotTurn.Record(
            tenantId, conversation.Id, trigger.Id, lead?.ProductLineId, profile.Version, model.Name,
            input: run.Input,
            output: run.Reply?.Text ?? run.Escalation?.Summary,
            toolCalls: run.Calls.Count == 0 ? null : JsonSerializer.Serialize(run.Calls, Json),
            outcome, reason,
            answered: run.Reply?.Answered,
            latencyMs: (int)watch.ElapsedMilliseconds,
            run.InputTokens, run.OutputTokens,
            errorCode: run.Failure,
            replyMessageId: replyId,
            now), ct);
    }

    // ── The model ────────────────────────────────────────────────────────────────────────────

    private async Task<BotRun> AskAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, WhatsAppMessage trigger,
        WhatsAppBotProfile profile, CancellationToken ct)
    {
        var settings = options.Value;
        var run = new BotRun();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));

        try
        {
            var (messages, transcript) = await HistoryAsync(conversation.Id, trigger, settings.MaxHistoryMessages, timeout.Token);
            run.Input = transcript;

            var alreadySent = await db.WhatsAppMessages
                .Where(m => m.ConversationId == conversation.Id
                         && m.Direction == WhatsAppDirection.Outbound
                         && m.OccurredAt > trigger.OccurredAt)
                .OrderBy(m => m.OccurredAt)
                .Select(m => m.Body)
                .Take(3)
                .ToListAsync(timeout.Token);

            var catalogue = await CatalogueAsync(tenantId, timeout.Token);
            var product = ProductOf(profile, lead, catalogue);

            var system = WhatsAppBotPrompt.Build(
                profile,
                product?.Name,
                profile.FieldsToCollect.ToDictionary(f => f, f => IsFilled(lead, f)),
                alreadySent.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => WhatsAppBotRedactor.Redact(b))!.ToList()!,
                clock.GetUtcNow());

            var tools = WhatsAppBotTools.For(profile);

            for (var call = 0; call < MaxModelCalls; call++)
            {
                var response = await model.CompleteAsync(new BotModelRequest(system, messages.ToList(), tools), timeout.Token);

                run.InputTokens = (run.InputTokens ?? 0) + (response.InputTokens ?? 0);
                run.OutputTokens = (run.OutputTokens ?? 0) + (response.OutputTokens ?? 0);

                var uses = response.Content.OfType<BotToolUseBlock>().ToList();

                if (uses.Count == 0)
                {
                    run.Failure = "BOT_NO_TOOL_CALL";
                    return run;
                }

                messages.Add(new BotMessage("assistant", response.Content));
                var results = new List<BotBlock>();

                foreach (var use in uses)
                {
                    run.Calls.Add(new BotToolCall(use.Name, use.Input.GetRawText()));
                    results.Add(Execute(use, run, profile, lead, product, catalogue));
                }

                // A handover wins over a reply sent in the same breath.
                if (run.Escalation is not null || run.Reply is not null) return run;

                messages.Add(new BotMessage("user", results));
            }

            run.Failure = "BOT_NO_DECISION";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            run.Failure = "BOT_TIMEOUT";
        }
        catch (BotModelException ex)
        {
            logger.LogWarning("WhatsApp bot: the model failed ({Code}): {Detail}", ex.Code, ex.Message);
            run.Failure = ex.Code;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "WhatsApp bot turn of conversation {ConversationId} failed.", conversation.Id);
            run.Failure = "BOT_ERROR";
        }

        // A decision that came with a failure after it is still a decision; a failure cancels
        // nothing that was decided.
        if (run.Escalation is not null || run.Reply is not null) run.Failure = null;
        return run;
    }

    private BotBlock Execute(
        BotToolUseBlock use, BotRun run, WhatsAppBotProfile profile, Lead? lead,
        ProductSummary? product, IReadOnlyList<ProductSummary> catalogue)
    {
        BotToolResultBlock Ok(string content) => new(use.Id, content, IsError: false);
        BotToolResultBlock Error(string content) => new(use.Id, content, IsError: true);

        switch (use.Name)
        {
            case WhatsAppBotTools.GetProductInfo:
            {
                var info = new JsonObject
                {
                    ["product"] = product is null ? null : new JsonObject { ["name"] = product.Name, ["category"] = product.Category },
                    ["faq"] = new JsonArray(profile.Faq.Select(f => (JsonNode?)new JsonObject
                    {
                        ["question"] = f.Question,
                        ["answer"] = f.Answer,
                    }).ToArray()),
                };

                // The general profile talks to somebody whose product is not known yet: it may
                // name what the institution offers, never more than the names.
                if (product is null)
                {
                    info["catalogue"] = new JsonArray(catalogue
                        .Where(p => p.IsActive)
                        .Select(p => (JsonNode?)new JsonObject { ["name"] = p.Name, ["category"] = p.Category })
                        .ToArray());
                }

                return Ok(info.ToJsonString());
            }

            case WhatsAppBotTools.UpdateLeadField:
            {
                var field = String(use.Input, "field")?.Trim().ToUpperInvariant();
                var value = String(use.Input, "value")?.Trim();

                if (field is null || !profile.FieldsToCollect.Contains(field))
                    return Error("Ce champ ne fait pas partie des informations à recueillir.");
                if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
                    return Error("Valeur vide ou trop longue.");
                if (lead is null)
                    return Error("Aucune fiche n’est rattachée à cette conversation.");

                var error = ApplyField(lead, field, value);
                if (error is not null) return Error(error);

                run.Collected.Add(field);
                return Ok("Enregistré.");
            }

            case WhatsAppBotTools.EscalateToHuman:
            {
                var reason = String(use.Input, "reason")?.Trim().ToLowerInvariant();
                var summary = String(use.Input, "summary")?.Trim();

                if (reason is null || !WhatsAppBotReasons.ModelReasons.TryGetValue(reason, out var code))
                    return Error($"Raison obligatoire, parmi : {string.Join(", ", WhatsAppBotReasons.ModelReasons.Keys)}.");
                if (string.IsNullOrWhiteSpace(summary))
                    return Error("Le résumé est obligatoire.");

                run.Escalation = new BotHandover(code, summary.Length <= 2000 ? summary : summary[..2000]);
                return Ok("Transmis.");
            }

            case WhatsAppBotTools.SendReply:
            {
                var text = String(use.Input, "text")?.Trim();

                if (string.IsNullOrWhiteSpace(text))
                    return Error("Le texte de la réponse est vide.");

                var answered = use.Input.ValueKind == JsonValueKind.Object
                    && use.Input.TryGetProperty("answered_question", out var a)
                    && a.ValueKind == JsonValueKind.True;

                run.Reply = new BotReply(text.Length <= 4000 ? text : text[..4000], answered);
                return Ok("Envoyé.");
            }

            default:
                return Error("Outil inconnu.");
        }
    }

    /// <summary>Writes one collected field on the lead. Returns the refusal in words the model can act on.</summary>
    private string? ApplyField(Lead lead, string field, string value)
    {
        Sankore.Shared.Kernel.Result result;

        switch (field)
        {
            case WhatsAppBotFields.FirstName:
                result = lead.Update(null, firstName: value, lastName: null, null, null, null, null, null, null, null, null, null);
                break;

            case WhatsAppBotFields.LastName:
                result = lead.Update(null, firstName: null, lastName: value, null, null, null, null, null, null, null, null, null);
                break;

            case WhatsAppBotFields.Email:
                if (!System.Net.Mail.MailAddress.TryCreate(value, out var address) || !address.Host.Contains('.'))
                    return "Adresse e-mail invalide.";
                result = lead.SetEmail(address.Address, emailIndexer.Compute(address.Address));
                break;

            case WhatsAppBotFields.City:
                result = lead.EnrichPrincipalIdentity(null, null, null, null, null, null, null, city: value, null, clock.GetUtcNow());
                break;

            case WhatsAppBotFields.DesiredAmount:
                var amount = ParseAmount(value);
                if (amount is null) return "Montant illisible : donne un nombre, par exemple 500000.";
                result = lead.Update(null, null, null, null, null, null, desiredAmount: amount, null, null, null, null, null);
                break;

            default:
                return "Champ non pris en charge.";
        }

        return result.IsSuccess ? null : "La fiche ne peut plus être modifiée.";
    }

    /// <summary>« 500 000 FCFA », « 1,5 million », « 750k », « 2 000 000 » — XOF unless another currency is named.</summary>
    internal static Money? ParseAmount(string text)
    {
        var match = Regex.Match(text, @"\d[\d\s  .,]*");
        if (!match.Success) return null;

        var raw = Regex.Replace(match.Value.Trim(), @"[\s  ]", "");

        // A dot or a comma followed by exactly three digits is a thousands separator; otherwise
        // it is the decimal mark (« 1,5 million »).
        raw = Regex.Replace(raw, @"[.,](?=\d{3}(?:[.,]|$))", "");
        raw = raw.Replace(',', '.');

        if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;

        var lower = WhatsAppBotText.Normalize(text);
        if (Regex.IsMatch(lower, @"\bmilliards?\b")) amount *= 1_000_000_000m;
        else if (Regex.IsMatch(lower, @"\bmillions?\b")) amount *= 1_000_000m;
        else if (Regex.IsMatch(lower, @"\d\s*k\b")) amount *= 1_000m;

        if (amount <= 0 || amount >= 1_000_000_000_000m) return null;

        var currency = lower.Contains("eur") || text.Contains('€') ? "EUR"
            : lower.Contains("usd") || lower.Contains("dollar") || text.Contains('$') ? "USD"
            : "XOF";

        return new Money(decimal.Round(amount, 2), currency);
    }

    private static bool IsFilled(Lead? lead, string field) => lead is not null && field switch
    {
        WhatsAppBotFields.FirstName => !string.IsNullOrWhiteSpace(lead.FirstName),
        WhatsAppBotFields.LastName => !string.IsNullOrWhiteSpace(lead.LastName),
        WhatsAppBotFields.Email => !string.IsNullOrWhiteSpace(lead.Email),
        WhatsAppBotFields.City => !string.IsNullOrWhiteSpace(lead.PrincipalCity),
        WhatsAppBotFields.DesiredAmount => lead.DesiredAmount is { Amount: > 0 },
        _ => false,
    };

    /// <summary>
    /// The latest messages up to the one being answered, minimised (WA-BE-11) and shaped the way a
    /// model expects: alternating roles, starting with the customer.
    /// </summary>
    private async Task<(List<BotMessage> Messages, string Transcript)> HistoryAsync(
        Guid conversationId, WhatsAppMessage trigger, int max, CancellationToken ct)
    {
        var recent = await db.WhatsAppMessages
            .Where(m => m.ConversationId == conversationId && m.OccurredAt <= trigger.OccurredAt)
            .OrderByDescending(m => m.OccurredAt)
            .Take(Math.Clamp(max, 1, 50))
            .ToListAsync(ct);

        recent.Reverse();

        var turns = new List<(string Role, string Text)>();

        foreach (var m in recent)
        {
            if (m.Direction == WhatsAppDirection.Outbound && m.Status == WhatsAppMessageStatus.Failed) continue;

            var role = m.Direction == WhatsAppDirection.Inbound ? "user" : "assistant";
            var text = m.Kind switch
            {
                WhatsAppMessageKind.Text or WhatsAppMessageKind.Button => WhatsAppBotRedactor.Redact(m.Body),
                WhatsAppMessageKind.Template => WhatsAppBotRedactor.Redact(m.Body ?? $"[modèle {m.TemplateName}]"),
                _ => $"[{m.Kind.ToString().ToLowerInvariant()}]",
            };

            if (string.IsNullOrWhiteSpace(text)) continue;

            if (turns.Count > 0 && turns[^1].Role == role)
                turns[^1] = (role, turns[^1].Text + "\n" + text);
            else
                turns.Add((role, text));
        }

        // A model conversation opens with the customer.
        while (turns.Count > 0 && turns[0].Role == "assistant") turns.RemoveAt(0);

        if (turns.Count == 0 || turns[^1].Role != "user")
            turns.Add(("user", WhatsAppBotRedactor.Redact(trigger.Body) is { Length: > 0 } t ? t : "[message]"));

        var messages = turns.Select(t => new BotMessage(t.Role, [new BotTextBlock(t.Text)])).ToList();
        var transcript = string.Join("\n", turns.Select(t => $"{(t.Role == "user" ? "client" : "institution")}: {t.Text}"));

        return (messages, transcript);
    }

    private async Task<IReadOnlyList<ProductSummary>> CatalogueAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            return await admin.ListProductsAsync(tenantId, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The FAQ still answers; only the product's name is missing.
            logger.LogWarning("WhatsApp bot: the product catalogue could not be read ({Reason}).", ex.GetType().Name);
            return [];
        }
    }

    private static ProductSummary? ProductOf(WhatsAppBotProfile profile, Lead? lead, IReadOnlyList<ProductSummary> catalogue)
    {
        var line = profile.IsGeneral ? lead?.ProductLineId : profile.ProductLineId;
        return line is { } id && id != Guid.Empty ? catalogue.FirstOrDefault(p => p.Id == id) : null;
    }

    private Task<bool> HasNewerInboundAsync(Guid conversationId, WhatsAppMessage trigger, CancellationToken ct) =>
        db.WhatsAppMessages.AnyAsync(m => m.ConversationId == conversationId
                                       && m.Direction == WhatsAppDirection.Inbound
                                       && m.Id != trigger.Id
                                       && (m.OccurredAt > trigger.OccurredAt
                                           || (m.OccurredAt == trigger.OccurredAt && m.CreatedAt > trigger.CreatedAt)), ct);

    private async Task CommitAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, WhatsAppBotTurn turn, CancellationToken ct)
    {
        db.WhatsAppBotTurns.Add(turn);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A webhook moved the conversation between our read and our write — a new message,
            // whose own turn will answer. Nothing of this one is kept, not even the turn row, so a
            // redelivery of this event may run it again against the new state.
            logger.LogInformation("WhatsApp bot turn of conversation {ConversationId} superseded by a concurrent change.", conversation.Id);
            return;
        }

        await WhatsAppRealtime.PushAsync(
            realtime, logger, tenantId,
            [lead?.CurrentAssignedId, lead?.OwnerId, conversation.TargetAgentId, conversation.OwnerUserId],
            WhatsAppRealtime.ConversationUpdated, conversation.LeadId, conversation.Id, turn.ReplyMessageId ?? turn.InboundMessageId, ct);
    }

    private static string? String(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private sealed class BotRun
    {
        public string? Input { get; set; }
        public BotReply? Reply { get; set; }
        public BotHandover? Escalation { get; set; }
        public string? Failure { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public List<BotToolCall> Calls { get; } = [];
        public List<string> Collected { get; } = [];
    }

    private sealed record BotReply(string Text, bool Answered);

    private sealed record BotHandover(string Reason, string Summary);

    private sealed record BotToolCall(string Name, string Input);
}
