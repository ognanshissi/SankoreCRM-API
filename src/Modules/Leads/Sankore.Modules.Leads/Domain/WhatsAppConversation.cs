namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One WhatsApp exchange between the institution's number and one customer (WA-BE-06 →
/// WA-BE-10), filed on a lead, a client, or — for a number nobody recognises — on nobody yet.
///
/// <para>
/// <b>Keyed on the pair (number of the institution, number of the customer)</b>, not on the
/// lead: a reply carries a telephone number and nothing else, and it is by that number, through
/// <see cref="ContactPhoneBlindIndex"/>, that it finds its conversation. One customer writing to
/// two numbers of the same institution holds two conversations, because each number has its own
/// 24-hour window at Meta.
/// </para>
///
/// <para>
/// <b>The 24-hour window is a fact of the provider, recorded here</b> (WA-BE-10):
/// <see cref="WindowExpiresAt"/> is the last customer message plus 24 hours, and outside it only a
/// template may be sent. A conversation that never received a message has no window at all.
/// </para>
///
/// <para>
/// The customer's number and profile name are personal data and are encrypted in their columns;
/// the blind index is what is compared.
/// </para>
/// </summary>
public sealed class WhatsAppConversation : ITenant
{
    /// <summary>How long Meta keeps the customer-service window open after the customer writes.</summary>
    public static readonly TimeSpan ServiceWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a template stays « the last word » before another may be sent without the force
    /// permission (WA-BE-06).
    /// </summary>
    public static readonly TimeSpan TemplateCooldown = TimeSpan.FromHours(24);

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>The institution's number this exchange runs on — an integration connection id.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>The lead it is filed on. Null for a client's conversation or an unattached one.</summary>
    public Guid? LeadId { get; private set; }

    /// <summary>The client it is filed on, when the number belongs to a client and no open lead.</summary>
    public Guid? ClientId { get; private set; }

    /// <summary>The customer's number in E.164. Plaintext here, ciphertext in the column.</summary>
    public string ContactPhone { get; private set; } = default!;

    /// <summary>HMAC of <see cref="ContactPhone"/> under M13's key — the only thing ever compared.</summary>
    public string ContactPhoneBlindIndex { get; private set; } = default!;

    /// <summary>What the customer's WhatsApp profile calls them. Encrypted. Not an identity.</summary>
    public string? ContactName { get; private set; }

    public WhatsAppConversationStatus Status { get; private set; }

    /// <summary>
    /// True when the number matched several records and this one was chosen (WA-BE-09). The
    /// screen says so, so an agent can correct a family handset filed on the wrong person.
    /// </summary>
    public bool AmbiguousMatch { get; private set; }

    public DateTimeOffset? WindowExpiresAt { get; private set; }
    public DateTimeOffset? LastInboundAt { get; private set; }
    public DateTimeOffset? LastOutboundAt { get; private set; }

    /// <summary>When the last template went out — what the 24-hour rule of WA-BE-06 counts from.</summary>
    public DateTimeOffset? LastTemplateSentAt { get; private set; }

    public DateTimeOffset LastMessageAt { get; private set; }

    /// <summary>Customer messages nobody has looked at yet.</summary>
    public int UnreadCount { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    // ── Human takeover (WA-BE-14 → WA-BE-17) ─────────────────────────────

    /// <summary>The agent holding the conversation (<see cref="WhatsAppConversationStatus.Human"/>). One at a time.</summary>
    public Guid? OwnerUserId { get; private set; }

    public DateTimeOffset? TakenOverAt { get; private set; }

    /// <summary>
    /// The agent expected to take it over while it is <see cref="WhatsAppConversationStatus.PendingHuman"/>:
    /// the lead's agent, or whoever the dispatching chose. Null when nobody could be found — the
    /// supervisors' queue.
    /// </summary>
    public Guid? TargetAgentId { get; private set; }

    /// <summary>Since when somebody must answer — the start of the takeover SLA.</summary>
    public DateTimeOffset? PendingSince { get; private set; }

    /// <summary>When the takeover SLA runs out. Null when no SLA is configured.</summary>
    public DateTimeOffset? TakeoverDeadline { get; private set; }

    /// <summary>When the SLA was found breached — once per escalation, so a supervisor is told once.</summary>
    public DateTimeOffset? TakeoverBreachedAt { get; private set; }

    /// <summary>
    /// Why it went to a person: <c>CUSTOMER_REPLIED</c>, a rule's <c>BOT_*</c> code or the bot's own reason.
    /// A code, never the customer's words.
    /// </summary>
    public string? EscalationReason { get; private set; }

    /// <summary>What the bot understood, for the agent who takes over (WA-BE-13). Encrypted.</summary>
    public string? EscalationSummary { get; private set; }

    /// <summary>Why it was closed — the agent's words, or <c>OPT_OUT</c> after a STOP.</summary>
    public string? CloseReason { get; private set; }

    public Guid? ClosedBy { get; private set; }

    /// <summary>When the holder was told the window closes within the hour — once per window.</summary>
    public DateTimeOffset? WindowWarningSentAt { get; private set; }

    // ── Bot (WA-BE-11 → WA-BE-13) ───────────────────────────────────────

    /// <summary>
    /// Consecutive turns in which the bot said it could not answer. A useful answer resets it; the
    /// profile's threshold hands the conversation over.
    /// </summary>
    public int BotUnansweredTurns { get; private set; }

    /// <summary>
    /// Codes of the lead fields the bot wrote during its current session — CODES only, the values
    /// are read off the lead, so the summary shows what the record says now and nothing is stored
    /// twice.
    /// </summary>
    public List<string> BotCollectedFields { get; private set; } = [];

    // ── Prospect collected before a lead exists ──────────────────────────

    /// <summary>
    /// An unknown number the bot is talking to BEFORE any lead exists
    /// (<see cref="LeadSettingKeys.WhatsAppCreateLeadAfterCollection"/>): what the bot collects is
    /// held in <see cref="PendingLeadFieldsJson"/> until the lead is created. Distinguishes this
    /// conversation from an unattached one the tenant chose not to turn into a lead, which gets no bot.
    /// </summary>
    public bool CollectingProspect { get; private set; }

    /// <summary>
    /// Code → normalised value of what the bot collected while no lead existed, as a JSON object.
    /// Encrypted in its column: names, an e-mail, an amount. Emptied when the lead is created —
    /// from then on the lead holds them, and they are not kept twice.
    /// </summary>
    public string? PendingLeadFieldsJson { get; private set; }

    /// <summary>
    /// When the bot's collection completed the minimum the profile requires — once, so returning a
    /// conversation to the bot never hands it over again on that ground.
    /// </summary>
    public DateTimeOffset? BotLeadReadyAt { get; private set; }

    /// <summary>
    /// When the customer's identity was blanked (BNF-B-19, WA-BE-21). A column and not a reading
    /// of the number: the number is ciphertext and cannot be compared in SQL, and the media purge
    /// must find the conversations whose files are now owed for deletion.
    /// </summary>
    public DateTimeOffset? AnonymizedAt { get; private set; }

    /// <summary>PostgreSQL xmin — two webhooks for one conversation must not lose a counter.</summary>
    public uint Version { get; private set; }

    private WhatsAppConversation() { } // EF Core

    public static WhatsAppConversation Open(
        Guid tenantId,
        Guid connectionId,
        string contactPhoneE164,
        string contactPhoneBlindIndex,
        Guid? leadId,
        Guid? clientId,
        string? contactName,
        bool ambiguousMatch,
        DateTimeOffset now,
        bool collectingProspect = false)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("A conversation runs on a number.");
        if (string.IsNullOrWhiteSpace(contactPhoneE164) || string.IsNullOrWhiteSpace(contactPhoneBlindIndex))
            throw new DomainException("A conversation needs the customer's number.");

        return new WhatsAppConversation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            ContactPhone = contactPhoneE164,
            ContactPhoneBlindIndex = contactPhoneBlindIndex,
            LeadId = leadId,
            // A lead and a client at once would file the exchange twice; the lead wins, as in the
            // lookup order of WA-BE-09.
            ClientId = leadId is null ? clientId : null,
            ContactName = Clip(contactName, 200),
            AmbiguousMatch = ambiguousMatch,
            CollectingProspect = collectingProspect && leadId is null && clientId is null,
            Status = WhatsAppConversationStatus.AwaitingReply,
            LastMessageAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>Whether a free message may be sent now — the customer wrote less than 24 hours ago.</summary>
    public bool IsWindowOpen(DateTimeOffset now) => WindowExpiresAt is { } expires && expires > now;

    /// <summary>
    /// When the next template may be sent without the force permission, or null when now. A
    /// customer's answer lifts the rule: the 24 hours protect against insisting on SILENCE.
    /// </summary>
    public DateTimeOffset? NextTemplateAllowedAt(DateTimeOffset now)
    {
        if (LastTemplateSentAt is not { } sent) return null;
        if (LastInboundAt is { } answered && answered >= sent) return null;

        var allowed = sent + TemplateCooldown;
        return allowed > now ? allowed : null;
    }

    /// <summary>
    /// A template went out — from a closed conversation too, which it reopens. The status becomes
    /// « awaiting reply » only when nobody is already talking: a template sent into an open
    /// window does not take the conversation away from whoever holds it.
    /// </summary>
    public void RecordTemplateSent(DateTimeOffset now)
    {
        LastTemplateSentAt = now;
        LastOutboundAt = now;
        LastMessageAt = now;

        if (Status is WhatsAppConversationStatus.Closed || !IsWindowOpen(now))
        {
            Status = WhatsAppConversationStatus.AwaitingReply;
            ClosedAt = null;
        }
    }

    /// <summary>
    /// The customer wrote, with no bot available — lot 1 and 2's behaviour, kept for the callers
    /// that never route to a bot.
    /// </summary>
    /// <returns>True when the message ESCALATED the conversation.</returns>
    public bool RecordInbound(DateTimeOffset sentAt, string? profileName) =>
        RecordInbound(sentAt, profileName, botAvailable: false) == WhatsAppInboundRouting.Escalate;

    /// <summary>
    /// The customer wrote. The window reopens for 24 hours from the moment THEY wrote — the
    /// provider's clock, not ours — and the message goes to whoever answers it.
    ///
    /// <para>
    /// <b>Who answers.</b> An agent holding the conversation keeps it, and one already expected
    /// stays expected. A reply to a TEMPLATE goes to a person: an agent reached out and is waiting
    /// for that answer, a bot cutting in would answer for them. Anything else — a first message,
    /// a closed conversation reopened, a conversation the bot already holds — goes to the bot when
    /// one is available for this lead, and to a person otherwise, which is the specification's
    /// « bot désactivé = escalade directe ».
    /// </para>
    /// </summary>
    /// <param name="botAvailable">
    /// The tenant, the lead's product line and the model all allow the bot — resolved by the
    /// caller, which can read settings this aggregate cannot.
    /// </param>
    public WhatsAppInboundRouting RecordInbound(DateTimeOffset sentAt, string? profileName, bool botAvailable)
    {
        var previous = Status;
        var answeringTemplate = previous == WhatsAppConversationStatus.AwaitingReply && LastTemplateSentAt is not null;

        LastInboundAt = LastInboundAt is { } last && last > sentAt ? last : sentAt;

        var window = LastInboundAt.Value + ServiceWindow;
        WindowExpiresAt = WindowExpiresAt is { } current && current > window ? current : window;

        LastMessageAt = sentAt > LastMessageAt ? sentAt : LastMessageAt;
        UnreadCount++;

        if (!string.IsNullOrWhiteSpace(profileName)) ContactName = Clip(profileName, 200);

        // A new window: the holder may be warned again before it closes.
        WindowWarningSentAt = null;

        switch (previous)
        {
            case WhatsAppConversationStatus.Human:
            case WhatsAppConversationStatus.PendingHuman:
                return WhatsAppInboundRouting.None;

            case WhatsAppConversationStatus.Bot when botAvailable:
                return WhatsAppInboundRouting.BotContinues;

            case WhatsAppConversationStatus.AwaitingReply or WhatsAppConversationStatus.Closed
                when botAvailable && !answeringTemplate:
                Status = WhatsAppConversationStatus.Bot;
                ClosedAt = null;
                CloseReason = null;
                ClosedBy = null;
                BotUnansweredTurns = 0;
                BotCollectedFields = [];
                return WhatsAppInboundRouting.BotStarted;

            default:
                Status = WhatsAppConversationStatus.PendingHuman;
                ClosedAt = null;
                CloseReason = null;
                ClosedBy = null;
                return WhatsAppInboundRouting.Escalate;
        }
    }

    /// <summary>
    /// The bot answered — usefully or not, by its own declaration. Returns the consecutive
    /// count of turns without an answer, which the caller compares to the profile's threshold.
    /// </summary>
    public int RecordBotAnswer(bool answered)
    {
        BotUnansweredTurns = answered ? 0 : BotUnansweredTurns + 1;
        return BotUnansweredTurns;
    }

    /// <summary>The bot wrote this field on the lead; the escalation summary lists it.</summary>
    public void RecordBotCollected(string fieldCode)
    {
        if (!BotCollectedFields.Contains(fieldCode))
            BotCollectedFields = [.. BotCollectedFields, fieldCode];
    }

    /// <summary>What the bot collected while no lead existed — empty once the lead is created.</summary>
    public IReadOnlyDictionary<string, string> PendingLeadFields() =>
        string.IsNullOrWhiteSpace(PendingLeadFieldsJson)
            ? new Dictionary<string, string>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(PendingLeadFieldsJson) ?? [];

    /// <summary>Keeps one collected value until the lead exists. The latest value of a field wins.</summary>
    public void RecordPendingField(string code, string value)
    {
        if (!CollectingProspect || LeadId is not null) return;

        var fields = new Dictionary<string, string>(PendingLeadFields()) { [code] = value };
        PendingLeadFieldsJson = System.Text.Json.JsonSerializer.Serialize(fields);
        RecordBotCollected(code);
    }

    /// <summary>
    /// The minimum is complete, for the first time. Returns false when it already was — the
    /// handover on that ground happens once per conversation.
    /// </summary>
    public bool MarkLeadReady(DateTimeOffset now)
    {
        if (BotLeadReadyAt is not null) return false;
        BotLeadReadyAt = now;
        return true;
    }

    /// <summary>
    /// Waiting for a person (WA-BE-14): who is expected, why, and until when. Called when
    /// <see cref="RecordInbound"/> escalated, and by a reassignment of a pending conversation.
    /// </summary>
    public void Escalate(string reason, string? summary, Guid? targetAgentId, TimeSpan? sla, DateTimeOffset now)
    {
        Status = WhatsAppConversationStatus.PendingHuman;
        EscalationReason = Clip(reason, 60);
        EscalationSummary = Clip(summary, 2000);
        TargetAgentId = targetAgentId;
        OwnerUserId = null;
        TakenOverAt = null;
        PendingSince = now;
        TakeoverDeadline = sla is { } s && s > TimeSpan.Zero ? now + s : null;
        TakeoverBreachedAt = null;
    }

    /// <summary>
    /// An agent takes the conversation (WA-BE-16). One holder at a time: another agent's
    /// conversation must be reassigned first, by a supervisor — a takeover that silently pushed a
    /// colleague out would have two people answering one customer.
    /// </summary>
    public Result TakeOver(Guid agentId, DateTimeOffset now)
    {
        if (agentId == Guid.Empty) return Result.Fail("WHATSAPP_AGENT_REQUIRED");

        if (Status == WhatsAppConversationStatus.Human)
        {
            return OwnerUserId == agentId
                ? Result.Ok()
                : Result.Fail("WHATSAPP_CONVERSATION_HELD_BY_ANOTHER");
        }

        Status = WhatsAppConversationStatus.Human;
        OwnerUserId = agentId;
        TakenOverAt = now;
        TargetAgentId = null;
        TakeoverDeadline = null;
        ClosedAt = null;
        CloseReason = null;
        ClosedBy = null;
        return Result.Ok();
    }

    /// <summary>
    /// A supervisor hands it to another agent (WA-FE-08 « Réassigner »): the holder changes when
    /// somebody holds it, the expected agent when it is still waiting — with a fresh SLA, since
    /// the new agent has not been waited on yet.
    /// </summary>
    public Result Reassign(Guid agentId, TimeSpan? sla, DateTimeOffset now)
    {
        if (agentId == Guid.Empty) return Result.Fail("WHATSAPP_AGENT_REQUIRED");
        if (Status == WhatsAppConversationStatus.Closed) return Result.Fail("WHATSAPP_CONVERSATION_CLOSED");

        if (Status == WhatsAppConversationStatus.Human)
        {
            OwnerUserId = agentId;
            TakenOverAt = now;
            return Result.Ok();
        }

        Escalate(EscalationReason ?? "REASSIGNED", EscalationSummary, agentId, sla, now);
        return Result.Ok();
    }

    /// <summary>
    /// Names the agent expected once the dispatching found one — the SLA already running is kept:
    /// the customer has been waiting since the escalation, not since the dispatch.
    /// </summary>
    public void SetTarget(Guid agentId)
    {
        if (Status == WhatsAppConversationStatus.PendingHuman) TargetAgentId = agentId;
    }

    /// <summary>The SLA ran out (WA-BE-15): recorded once, so the supervisor is told once.</summary>
    public bool MarkTakeoverBreached(DateTimeOffset now)
    {
        if (Status != WhatsAppConversationStatus.PendingHuman || TakeoverBreachedAt is not null) return false;
        if (TakeoverDeadline is not { } deadline || deadline > now) return false;

        TakeoverBreachedAt = now;
        return true;
    }

    /// <summary>
    /// Back to the bot (WA-BE-17). When the bot is switched off for this lead, the next customer
    /// message escalates again — which is what « bot désactivé = escalade directe » asks for.
    /// </summary>
    public Result ReturnToBot(Guid agentId)
    {
        if (Status == WhatsAppConversationStatus.Closed) return Result.Fail("WHATSAPP_CONVERSATION_CLOSED");
        if (Status == WhatsAppConversationStatus.Human && OwnerUserId != agentId)
            return Result.Fail("WHATSAPP_CONVERSATION_HELD_BY_ANOTHER");

        Status = WhatsAppConversationStatus.Bot;
        OwnerUserId = null;
        TargetAgentId = null;
        TakeoverDeadline = null;
        PendingSince = null;
        BotUnansweredTurns = 0;
        return Result.Ok();
    }

    /// <summary>
    /// Ended, with its reason (WA-BE-17). A new message from the customer reopens it.
    /// </summary>
    public Result Close(string reason, Guid? closedBy, DateTimeOffset now)
    {
        if (Status == WhatsAppConversationStatus.Closed) return Result.Ok();
        if (string.IsNullOrWhiteSpace(reason)) return Result.Fail("WHATSAPP_CLOSE_REASON_REQUIRED");

        Status = WhatsAppConversationStatus.Closed;
        CloseReason = Clip(reason, 300);
        ClosedBy = closedBy;
        ClosedAt = now;
        OwnerUserId = null;
        TargetAgentId = null;
        TakeoverDeadline = null;
        PendingSince = null;
        UnreadCount = 0;
        return Result.Ok();
    }

    /// <summary>A message of ours went out — a free reply or a media.</summary>
    public void RecordOutbound(DateTimeOffset now)
    {
        LastOutboundAt = now;
        LastMessageAt = now;
    }

    /// <summary>The holder was warned that the window closes within the hour.</summary>
    public void MarkWindowWarningSent(DateTimeOffset now) => WindowWarningSentAt = now;

    /// <summary>An agent opened the thread.</summary>
    public void MarkRead() => UnreadCount = 0;

    /// <summary>Files an unattached conversation on a lead (WA-BE-09's « non rattachés » queue).</summary>
    public void AttachToLead(Guid leadId)
    {
        LeadId = leadId;
        ClientId = null;
        AmbiguousMatch = false;

        // Whatever was pending is on the lead now (WhatsAppProspects copies it before attaching),
        // or was never wanted on it (an agent filing by hand). Either way it is not kept twice.
        CollectingProspect = false;
        PendingLeadFieldsJson = null;
    }

    /// <summary>
    /// Blanks who the customer is, keeping that an exchange took place and when (BNF-B-19, with
    /// the lead it is filed on).
    ///
    /// <para>
    /// The blind index is replaced by a value no number can produce, so the next message from that
    /// number opens a NEW conversation instead of landing in an anonymised one — a customer who
    /// writes again after the retention period is a new contact, not a resurrected record.
    /// </para>
    /// </summary>
    internal void Anonymize(DateTimeOffset now)
    {
        AnonymizedAt ??= now;
        ContactPhone = "anonymized";
        ContactPhoneBlindIndex = $"anonymized:{Id:N}";
        ContactName = null;
        EscalationSummary = null;
        CloseReason = null;
        PendingLeadFieldsJson = null;
        Status = WhatsAppConversationStatus.Closed;
        UnreadCount = 0;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
