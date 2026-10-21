namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Who holds a WhatsApp conversation (WA-BE-06, WA-BE-14).
///
/// <para>
/// <b>Lot 1 writes three of the five values.</b> A template opens the exchange
/// (<see cref="AwaitingReply"/>); the customer's answer hands it to a person
/// (<see cref="PendingHuman"/>) because no bot answers yet — the specification's own rule for a
/// bot that is switched off, « désactivé = escalade directe »; <see cref="Closed"/> ends it. The
/// bot and the agent who took over arrive with lots 2 and 3, and are declared now so the column
/// does not change meaning later.
/// </para>
/// </summary>
public enum WhatsAppConversationStatus
{
    /// <summary>A template was sent; the customer has not answered.</summary>
    AwaitingReply,

    /// <summary>The bot is answering (lot 3).</summary>
    Bot,

    /// <summary>The customer wrote and a person must answer — « En attente d'un agent ».</summary>
    PendingHuman,

    /// <summary>An agent took the conversation over (lot 2).</summary>
    Human,

    /// <summary>Ended. A new message from the customer reopens it.</summary>
    Closed,
}

/// <summary>What a customer's message asks of the caller of <c>RecordInbound</c>.</summary>
public enum WhatsAppInboundRouting
{
    /// <summary>Somebody already holds it or is expected — nothing to start.</summary>
    None,

    /// <summary>It now waits for a person: name the agent, start the SLA, send the waiting message.</summary>
    Escalate,

    /// <summary>The bot opens the conversation: welcome message, then a turn.</summary>
    BotStarted,

    /// <summary>The bot already holds it: a turn.</summary>
    BotContinues,
}

/// <summary>Which way a message travelled.</summary>
public enum WhatsAppDirection
{
    Inbound,
    Outbound,
}

/// <summary>Who wrote a message — the colour and the label of its bubble.</summary>
public enum WhatsAppAuthor
{
    /// <summary>The customer.</summary>
    Contact,

    /// <summary>An agent of the institution, named by <c>AuthorUserId</c>.</summary>
    Agent,

    /// <summary>The bot (lot 3).</summary>
    Bot,

    /// <summary>
    /// A message the platform sends on its own — the waiting message of an escalation, the STOP
    /// confirmation. Not the bot: nobody conversed, a rule fired.
    /// </summary>
    System,
}

/// <summary>What a message is, as the bubble renders it.</summary>
public enum WhatsAppMessageKind
{
    Text,
    Template,
    Image,
    Document,
    Audio,
    Video,
    Sticker,
    Location,
    Contacts,

    /// <summary>A button pressed — a template's quick reply or an interactive message's choice.</summary>
    Button,
    Reaction,

    /// <summary>A type this platform does not render. Shown as « message non pris en charge ».</summary>
    Unsupported,
}

/// <summary>
/// Where a message stands (WA-BE-08). Ordered: an earlier status never overwrites a later one —
/// Meta does not promise to deliver statuses in order, and « lu » followed by a late « livré » must
/// stay « lu ».
/// </summary>
public enum WhatsAppMessageStatus
{
    /// <summary>Written to the outbox, not yet handed to the provider.</summary>
    Queued = 0,

    /// <summary>
    /// Being handed to the provider. A message found in this state after a crash is NOT sent
    /// again: whether the provider accepted it cannot be known, and a duplicate template is what
    /// the send rule exists to avoid.
    /// </summary>
    Sending = 1,

    /// <summary>The provider accepted it. Not yet a proof it reached the handset.</summary>
    Sent = 2,

    Delivered = 3,

    Read = 4,

    /// <summary>Refused or not delivered. The error code says why.</summary>
    Failed = 5,

    /// <summary>An inbound message. It has no delivery status of its own.</summary>
    Received = 6,
}

/// <summary>A template's standing at Meta, as the catalogue synchronisation last read it.</summary>
public enum WhatsAppTemplateStatus
{
    Approved,
    Pending,
    Rejected,
    Paused,
    Disabled,

    /// <summary>A status word this platform does not know. Never offered to an agent.</summary>
    Other,
}

/// <summary>
/// The lead fields a template variable may be filled from (WA-BE-04). A CLOSED list, so the
/// mapping cannot name a field this module would have to resolve dynamically — and so no
/// mapping can pull a field a template should never carry, such as an identity number.
/// </summary>
public enum WhatsAppTemplateField
{
    LeadFirstName,
    LeadLastName,
    LeadFullName,
    InterestedProduct,
    AgentFirstName,
    AgentFullName,
}
