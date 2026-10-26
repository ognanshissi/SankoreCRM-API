namespace Sankore.Modules.Leads.Features.WhatsApp.Bot;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.RecalculateLeadScore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel.Realtime;
using Sankore.Shared.Kernel;
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
/// One bot turn (WA-BE-11 → WA-BE-13): the deterministic rules, then the model with its one
/// tool, then the reply or the handover — and a <see cref="WhatsAppBotTurn"/> either way.
///
/// <para>
/// <b>Every answer is a tool call.</b> The model ends its turn with <c>respond</c>: a reply (text
/// plus « did I answer the question ») or a handover (a reason from a closed list plus a summary),
/// with what the customer just gave alongside. Nothing it writes outside the tool reaches the
/// customer, so the engine never has to guess whether a paragraph was meant as an answer, and the
/// « no useful answer » rule counts a declaration instead of a guess.
/// </para>
///
/// <para>
/// <b>One call per message, two at most.</b> The FAQ is in the prompt and the fields ride on the
/// decision, so a well-formed answer is the whole turn. The second call exists only for a decision
/// the engine refuses (a reply without text, a handover without a reason). A field the engine
/// refuses — an e-mail that is not one — costs nothing: it is dropped, the reply still goes, and
/// the field is still « missing » in the next turn's prompt, so the bot asks again naturally.
/// </para>
///
/// <para>
/// <b>A prospect has no lead yet.</b> For an unknown number the bot may talk BEFORE any lead
/// exists (<see cref="LeadSettingKeys.WhatsAppCreateLeadAfterCollection"/>): what it collects
/// waits on the conversation, and <see cref="WhatsAppProspects"/> creates the lead when the
/// profile's minimum is complete — or at ANY handover first, because the person who takes over
/// needs a record to work on. Once the minimum is complete the bot thanks the customer and hands
/// over (<see cref="WhatsAppBotReasons.LeadReady"/>), once per conversation; on a conversation that
/// already had a lead the same rule applies when THIS turn's collection completed it.
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
    WhatsAppBotFieldValues fieldValues,
    WhatsAppProspects prospects,
    WhatsAppAudit audit,
    IRealtimeNotifier realtime,
    IOptions<WhatsAppBotOptions> options,
    ISender sender,
    TimeProvider clock,
    ILogger<WhatsAppBotEngine> logger,
    Complaints.WhatsAppComplaintDesk? complaintDesk = null)
{
    /// <summary>Model calls in one turn: the decision, and one correction if it was malformed or not a call to respond.</summary>
    internal const int MaxModelCalls = 2;

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
        var ctx = await fieldValues.ContextAsync(tenantId, ct);
        var profile = await profiles.ResolveForConversationAsync(tenantId, conversation, lead, ct);
        var turn = new TurnState(lead);

        if (profile is null)
        {
            // Switched off between the message and its turn: the specification's « désactivé =
            // escalade directe », applied late rather than not at all.
            await EnsureLeadAsync(tenantId, conversation, ctx, turn, ct);
            await escalation.EscalateAsync(tenantId, conversation, turn.Lead, WhatsAppBotReasons.Disabled,
                "Le bot n’est plus actif pour ce prospect.", now, profile: null, tellCustomer: true, ct);

            await FinishAsync(tenantId, conversation, turn, WhatsAppBotTurn.Record(
                tenantId, conversation.Id, trigger.Id, WhatsAppBotFieldValues.LineOf(turn.Lead, ctx.Catalogue), profileVersion: 0, RulesModel,
                input: null, output: null, toolCalls: null, WhatsAppBotTurnOutcome.RuleEscalated, WhatsAppBotReasons.Disabled,
                answered: null, latencyMs: 0, null, null, errorCode: null, replyMessageId: null, now), collected: false, ct);
            return;
        }

        var verdict = WhatsAppBotRules.BeforeModel(
            trigger.Kind, trigger.Body, lead?.Status, conversation.BotUnansweredTurns, profile);

        if (verdict is not null)
        {
            await EnsureLeadAsync(tenantId, conversation, ctx, turn, ct);
            await escalation.EscalateAsync(tenantId, conversation, turn.Lead, verdict.Reason, verdict.Summary, now, profile, tellCustomer: true, ct);

            await FinishAsync(tenantId, conversation, turn, WhatsAppBotTurn.Record(
                tenantId, conversation.Id, trigger.Id, WhatsAppBotFieldValues.LineOf(turn.Lead, ctx.Catalogue), profile.Version, RulesModel,
                input: null, output: null, toolCalls: null, WhatsAppBotTurnOutcome.RuleEscalated, verdict.Reason,
                answered: null, latencyMs: 0, null, null, errorCode: null, replyMessageId: null, now), collected: false, ct);
            return;
        }

        var watch = Stopwatch.StartNew();
        var run = await AskAsync(conversation, lead, trigger, profile, ctx, ct);
        watch.Stop();

        // Re-read what may have moved while the model was thinking. Only the conversation: the
        // lead carries the fields the bot just wrote, which are kept whatever happens next.
        await db.Entry(conversation).ReloadAsync(ct);

        foreach (var code in run.Collected) conversation.RecordBotCollected(code);
        await KeepPendingAsync(conversation, run, ctx, turn, ct);

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
            await EnsureLeadAsync(tenantId, conversation, ctx, turn, ct);
            await escalation.EscalateAsync(tenantId, conversation, turn.Lead, reason,
                run.Failure == "BOT_TIMEOUT"
                    ? "Le bot n’a pas répondu à temps ; le client attend un conseiller."
                    : "Le bot n’a pas pu répondre ; le client attend un conseiller.",
                now, profile, tellCustomer: true, ct);
        }
        else if (run.Escalation is { Reason: Complaints.WhatsAppComplaintDesk.EscalationReason }
                 && complaintDesk is not null
                 && await complaintDesk.TryStartFromBotAsync(tenantId, conversation, trigger, now, ct))
        {
            // A complaint the keywords missed: the intake takes it down rather than a person
            // being called for a form the customer can fill in here (2026-10-26).
            outcome = WhatsAppBotTurnOutcome.Replied;
            reason = Complaints.WhatsAppComplaintDesk.EscalationReason;
        }
        else if (run.Escalation is { } handover)
        {
            outcome = WhatsAppBotTurnOutcome.Escalated;
            reason = handover.Reason;
            await EnsureLeadAsync(tenantId, conversation, ctx, turn, ct);
            await escalation.EscalateAsync(tenantId, conversation, turn.Lead, handover.Reason, handover.Summary, now, profile, tellCustomer: true, ct);
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
                await EnsureLeadAsync(tenantId, conversation, ctx, turn, ct);
                await escalation.EscalateAsync(tenantId, conversation, turn.Lead, reason,
                    $"Le bot n’a pas su répondre {unanswered} fois de suite.", now, profile, tellCustomer: true, ct);
            }
            else
            {
                outcome = WhatsAppBotTurnOutcome.Replied;
                replyId = (await escalation.QueueAutomaticAsync(tenantId, conversation, WhatsAppAuthor.Bot, reply.Text, now, ct)).Id;

                // The minimum just became complete: the reply thanked the customer (the prompt
                // told the model to), the lead is created if it was waiting, and a person takes over.
                if (CompletedThisTurn(profile, conversation, turn.Lead, run, ctx) && conversation.MarkLeadReady(now))
                {
                    reason = WhatsAppBotReasons.LeadReady;
                    await EnsureLeadAsync(tenantId, conversation, ctx, turn, ct);
                    await escalation.EscalateAsync(tenantId, conversation, turn.Lead, reason,
                        LeadReadySummary(profile, conversation, turn.Lead, ctx), now, profile, tellCustomer: true, ct);
                }
            }
        }

        await FinishAsync(tenantId, conversation, turn, WhatsAppBotTurn.Record(
            tenantId, conversation.Id, trigger.Id, WhatsAppBotFieldValues.LineOf(turn.Lead, ctx.Catalogue), profile.Version, model.Name,
            input: run.Input,
            output: run.Reply?.Text ?? run.Escalation?.Summary,
            toolCalls: run.Calls.Count == 0 ? null : JsonSerializer.Serialize(run.Calls, Json),
            outcome, reason,
            answered: run.Reply?.Answered,
            latencyMs: (int)watch.ElapsedMilliseconds,
            run.InputTokens, run.OutputTokens,
            errorCode: run.Failure,
            replyMessageId: replyId,
            now), collected: run.Collected.Count > 0 || run.Pending.Count > 0, ct);
    }

    // ── The lead of a prospect ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The lead this turn works on — created from what the conversation holds when the bot was
    /// talking to a prospect. Idempotent within a turn.
    /// </summary>
    private async Task EnsureLeadAsync(
        Guid tenantId, WhatsAppConversation conversation, BotFieldContext ctx, TurnState turn, CancellationToken ct)
    {
        if (turn.Lead is not null || conversation is not { CollectingProspect: true, LeadId: null }) return;

        var (lead, consentRecorded) = await prospects.CreateFromConversationAsync(tenantId, conversation, ctx, null, ct);
        turn.Lead = lead;
        turn.LeadCreated = true;
        turn.ConsentRecorded = consentRecorded;
    }

    /// <summary>
    /// Puts what the model collected for a prospect on the conversation — or straight on the lead,
    /// when an agent filed the conversation on one while the model was thinking.
    /// </summary>
    private async Task KeepPendingAsync(
        WhatsAppConversation conversation, BotRun run, BotFieldContext ctx, TurnState turn, CancellationToken ct)
    {
        if (run.Pending.Count == 0) return;

        if (conversation is { CollectingProspect: true, LeadId: null })
        {
            foreach (var (code, value) in run.Pending) conversation.RecordPendingField(code, value);
            return;
        }

        if (conversation.LeadId is not { } leadId) return;

        turn.Lead ??= await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (turn.Lead is null) return;

        foreach (var (code, value) in run.Pending)
        {
            if (fieldValues.Write(turn.Lead, code, value, ctx) is null) conversation.RecordBotCollected(code);
        }
    }

    /// <summary>
    /// The profile's minimum is complete now, and THIS turn's collection is what completed it — a
    /// lead captured from a web form with its names already filled must not be handed over on the
    /// first « bonjour ».
    /// </summary>
    private static bool CompletedThisTurn(WhatsAppBotProfile profile, WhatsAppConversation conversation, Lead? lead, BotRun run, BotFieldContext ctx)
    {
        if (profile.FieldsRequiredForLead.Count == 0) return false;

        var pending = conversation.PendingLeadFields();
        var contributed = run.Collected.Concat(run.Pending.Keys).Any(profile.FieldsRequiredForLead.Contains);

        return contributed && profile.FieldsRequiredForLead.All(code => WhatsAppBotFieldValues.IsFilled(lead, pending, code, ctx));
    }

    /// <summary>What the agent who takes over reads: the minimum, collected, field by field.</summary>
    private static string LeadReadySummary(WhatsAppBotProfile profile, WhatsAppConversation conversation, Lead? lead, BotFieldContext ctx)
    {
        var pending = conversation.PendingLeadFields();
        var parts = profile.FieldsRequiredForLead
            .Select(code => $"{ctx.LabelOf(code)} : {WhatsAppBotFieldValues.Display(lead, pending, code, ctx) ?? "—"}");

        return "Le bot a recueilli les informations indispensables. " + string.Join(" ; ", parts) + ".";
    }

    // ── The model ────────────────────────────────────────────────────────────────────────────

    private async Task<BotRun> AskAsync(
        WhatsAppConversation conversation, Lead? lead, WhatsAppMessage trigger,
        WhatsAppBotProfile profile, BotFieldContext ctx, CancellationToken ct)
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

            var pending = conversation.PendingLeadFields();
            var product = ProductOf(profile, lead, pending, ctx.Catalogue);
            var offered = ctx.Catalogue.Where(p => p.IsActive).Select(p => p.Name).Distinct().ToList();

            // The products the customer may name: only while the product is unknown, and only when
            // this profile collects it. Known, the field is not offered at all.
            var productChoices = product is null && profile.FieldsToCollect.Contains(WhatsAppBotFields.Product)
                ? offered
                : [];

            // What the bot can actually record: a product with choices to make, a custom field the
            // form still has. Anything else would be a question whose answer goes nowhere.
            var collectable = profile.FieldsToCollect
                .Where(code => code switch
                {
                    WhatsAppBotFields.Product => productChoices.Count > 0 || WhatsAppBotFieldValues.IsFilled(lead, pending, code, ctx),
                    _ when WhatsAppBotFields.IsCustom(code) => ctx.CustomField(code) is not null,
                    _ => true,
                })
                .ToList();

            var system = new BotSystemPrompt(
                WhatsAppBotPrompt.Stable(profile, product?.Name, product is null ? offered : []),
                WhatsAppBotPrompt.Turn(
                    profile,
                    collectable.Select(code => new BotFieldStatus(
                        code,
                        ctx.LabelOf(code),
                        WhatsAppBotFieldValues.IsFilled(lead, pending, code, ctx),
                        profile.FieldsRequiredForLead.Contains(code))).ToList(),
                    alreadySent.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => WhatsAppBotRedactor.Redact(b))!.ToList()!,
                    clock.GetUtcNow()));

            var tools = WhatsAppBotTools.For(collectable, productChoices, ctx);

            for (var call = 0; call < MaxModelCalls; call++)
            {
                var response = await model.CompleteAsync(new BotModelRequest(system, messages.ToList(), tools, profile.Faq), timeout.Token);

                run.InputTokens = (run.InputTokens ?? 0) + (response.InputTokens ?? 0);
                run.OutputTokens = (run.OutputTokens ?? 0) + (response.OutputTokens ?? 0);

                var uses = response.Content.OfType<BotToolUseBlock>().ToList();

                if (uses.Count == 0)
                {
                    // Free text is never sent: the model is told once to put its answer in `respond`.
                    // The tool cannot be forced (current models refuse a forced tool_choice).
                    if (call + 1 >= MaxModelCalls || response.Content.Count == 0)
                    {
                        run.Failure = "BOT_NO_TOOL_CALL";
                        return run;
                    }

                    messages.Add(new BotMessage("assistant", response.Content));
                    messages.Add(new BotMessage("user",
                        [new BotTextBlock("Termine ce tour en appelant l’outil respond : ton texte n’a pas été envoyé au client.")]));
                    continue;
                }

                messages.Add(new BotMessage("assistant", response.Content));
                var results = new List<BotBlock>();

                foreach (var use in uses)
                {
                    run.Calls.Add(new BotToolCall(use.Name, use.Input.GetRawText()));
                    results.Add(Execute(use, run, collectable, lead, pending, ctx));
                }

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
        BotToolUseBlock use, BotRun run, IReadOnlyList<string> collectable, Lead? lead,
        IReadOnlyDictionary<string, string> pending, BotFieldContext ctx)
    {
        BotToolResultBlock Ok(string content) => new(use.Id, content, IsError: false);
        BotToolResultBlock Error(string content) => new(use.Id, content, IsError: true);

        if (use.Name != WhatsAppBotTools.Respond) return Error("Outil inconnu : appelle respond.");

        // The decision is checked BEFORE anything is written: a malformed call is sent back whole,
        // and its corrected version carries the fields again.
        var action = String(use.Input, "action")?.Trim().ToLowerInvariant();
        BotReply? reply = null;
        BotHandover? handover = null;

        switch (action)
        {
            case WhatsAppBotTools.Reply:
            {
                var text = String(use.Input, "text")?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return Error("Avec reply, le texte de la réponse est obligatoire.");

                var answered = use.Input.TryGetProperty("answered_question", out var a) && a.ValueKind == JsonValueKind.True;
                reply = new BotReply(text.Length <= 4000 ? text : text[..4000], answered);
                break;
            }

            case WhatsAppBotTools.Handover:
            {
                var reason = String(use.Input, "handover_reason")?.Trim().ToLowerInvariant();
                var summary = String(use.Input, "handover_summary")?.Trim();

                if (reason is null || !WhatsAppBotReasons.ModelReasons.TryGetValue(reason, out var code))
                    return Error($"Avec handover, handover_reason est obligatoire, parmi : {string.Join(", ", WhatsAppBotReasons.ModelReasons.Keys)}.");
                if (string.IsNullOrWhiteSpace(summary))
                    return Error("Avec handover, handover_summary est obligatoire.");

                handover = new BotHandover(code, summary.Length <= 2000 ? summary : summary[..2000]);
                break;
            }

            default:
                return Error("action est obligatoire : reply ou handover.");
        }

        // The fields, each on its own. A refused one is dropped without failing the turn — see the
        // class comment — and is still « missing » in the next turn's prompt.
        if (use.Input.TryGetProperty("collected", out var collected) && collected.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in collected.EnumerateObject())
            {
                var field = WhatsAppBotFields.Normalize(entry.Name);
                var raw = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;

                if (!collectable.Contains(field) || string.IsNullOrWhiteSpace(raw) || raw.Length > 200)
                {
                    run.Refused.Add(field);
                    continue;
                }

                // What this very call already kept counts: a product named twice is one product.
                var known = run.Pending.Count == 0
                    ? pending
                    : pending.Concat(run.Pending).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);

                var (value, refusal) = fieldValues.Normalize(field, raw, lead, known, ctx);

                if (refusal is null && lead is not null) refusal = fieldValues.Write(lead, field, value!, ctx);

                if (refusal is not null)
                {
                    logger.LogInformation("WhatsApp bot: field {Field} not recorded ({Refusal}).", field, refusal);
                    run.Refused.Add(field);
                    continue;
                }

                if (lead is not null)
                {
                    if (!run.Collected.Contains(field)) run.Collected.Add(field);
                }
                else
                {
                    run.Pending[field] = value!;
                }
            }
        }

        run.Reply = reply;
        run.Escalation = handover;
        return Ok(reply is not null ? "Envoyé." : "Transmis.");
    }

    /// <summary>
    /// What the bot collected moves the score — a name, a city and a neighbourhood are points of
    /// the calculator — and nothing else would recompute it: a WhatsApp lead used to keep the 0 it
    /// was captured with until an agent logged an activity. After the commit, and outside the
    /// turn's fate: a scoring failure is logged, never a reason to unsend a reply.
    /// </summary>
    private async Task RescoreAsync(Lead lead, CancellationToken ct)
    {
        if (lead.Status is LeadStatus.Converted or LeadStatus.Archived or LeadStatus.Lost or LeadStatus.Disqualified) return;

        try
        {
            await sender.Send(new RecalculateLeadScoreCommand(lead.Id, "WHATSAPP_BOT_COLLECTED"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "WhatsApp bot: the score of lead {LeadId} could not be recomputed.", lead.Id);
        }
    }

    /// <summary>
    /// « 500 000 FCFA », « 1,5 million », « 750k », « 2 000 000 » — in the institution's currency.
    /// An amount the customer names in another (« 3000 euros » for a franc CFA institution) is no
    /// amount: every amount of the institution is in its currency, and converting is not the bot's
    /// call — it asks again.
    /// </summary>
    internal static Money? ParseAmount(string text, string institution = InstitutionCurrencies.Default)
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

        var named = lower.Contains("eur") || text.Contains('€') ? "EUR"
            : lower.Contains("usd") || lower.Contains("dollar") || text.Contains('$') ? "USD"
            : Regex.IsMatch(lower, @"\b(f\s?cfa|cfa|xof|xaf)\b") ? "CFA"
            : null;
        var own = string.IsNullOrWhiteSpace(institution) ? InstitutionCurrencies.Default : institution.ToUpperInvariant();
        var matches = named is null
            || named == own
            || (named == "CFA" && own is "XOF" or "XAF");
        if (!matches) return null;

        return new Money(decimal.Round(amount, 2), own);
    }

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

            // The complaint intake's answers (a client number, a date of birth, the complaint) stay
            // between the customer and the institution: no model reads them, now or later.
            if (m.ExcludedFromModel) continue;

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

    /// <summary>The product the turn talks about: the profile's, else the lead's, else the one a prospect named.</summary>
    private static ProductSummary? ProductOf(
        WhatsAppBotProfile profile, Lead? lead, IReadOnlyDictionary<string, string> pending, IReadOnlyList<ProductSummary> catalogue)
    {
        var line = profile.IsGeneral
            ? WhatsAppBotFieldValues.LineOf(lead, catalogue) ?? WhatsAppBotFieldValues.PendingProduct(pending)
            : profile.ProductLineId;

        return line is { } id && id != Guid.Empty ? catalogue.FirstOrDefault(p => p.Id == id) : null;
    }

    private Task<bool> HasNewerInboundAsync(Guid conversationId, WhatsAppMessage trigger, CancellationToken ct) =>
        db.WhatsAppMessages.AnyAsync(m => m.ConversationId == conversationId
                                       && m.Direction == WhatsAppDirection.Inbound
                                       && m.Id != trigger.Id
                                       && (m.OccurredAt > trigger.OccurredAt
                                           || (m.OccurredAt == trigger.OccurredAt && m.CreatedAt > trigger.CreatedAt)), ct);

    /// <summary>
    /// Commits the turn — the conversation, the messages, the lead and anything the turn created —
    /// then, only once it is committed, what must not happen for a turn that rolled back: the push,
    /// the audit of a consent recorded for a new lead, the rescoring.
    /// </summary>
    private async Task FinishAsync(
        Guid tenantId, WhatsAppConversation conversation, TurnState state, WhatsAppBotTurn turn, bool collected, CancellationToken ct)
    {
        db.WhatsAppBotTurns.Add(turn);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A webhook moved the conversation between our read and our write — a new message,
            // whose own turn will answer. Nothing of this one is kept, not even the turn row (nor a
            // lead it was creating), so a redelivery of this event may run it again against the new state.
            logger.LogInformation("WhatsApp bot turn of conversation {ConversationId} superseded by a concurrent change.", conversation.Id);
            return;
        }

        var lead = state.Lead;

        await WhatsAppRealtime.PushAsync(
            realtime, logger, tenantId,
            [lead?.CurrentAssignedId, lead?.OwnerId, conversation.TargetAgentId, conversation.OwnerUserId],
            WhatsAppRealtime.ConversationUpdated, conversation.LeadId, conversation.Id, turn.ReplyMessageId ?? turn.InboundMessageId, ct);

        if (lead is null) return;

        if (state.ConsentRecorded)
            await audit.SystemAsync(tenantId, WhatsAppAudit.ConsentFromInboundMessage, conversation.Id, lead.Id, ct);

        if (collected || state.LeadCreated) await RescoreAsync(lead, ct);
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

        /// <summary>Code → normalised value, collected while no lead exists.</summary>
        public Dictionary<string, string> Pending { get; } = [];

        public List<string> Refused { get; } = [];
    }

    /// <summary>The lead a turn works on, which may be created during the turn.</summary>
    private sealed class TurnState(Lead? lead)
    {
        public Lead? Lead { get; set; } = lead;
        public bool LeadCreated { get; set; }
        public bool ConsentRecorded { get; set; }
    }

    private sealed record BotReply(string Text, bool Answered);

    private sealed record BotHandover(string Reason, string Summary);

    private sealed record BotToolCall(string Name, string Input);
}
