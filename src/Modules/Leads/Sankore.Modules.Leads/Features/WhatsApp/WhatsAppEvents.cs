namespace Sankore.Modules.Leads.Features.WhatsApp;

using Sankore.Shared.Kernel;

/// <summary>
/// Written to the outbox with the message it names (WA-BE-06): the message is handed to the
/// provider only if it was saved, and is handed even when the broker is down at that instant.
/// Identifiers only — the number and the values stay in the encrypted row.
/// </summary>
public sealed record WhatsAppMessageQueuedEvent(Guid TenantId, Guid MessageId) : IntegrationEventBase;

/// <summary>
/// A customer is waiting for a person (WA-BE-14). Its consumer finds an agent when none is
/// expected yet — M13's dispatching — and tells the one expected, by e-mail and in the bell.
/// </summary>
public sealed record WhatsAppConversationEscalatedEvent(Guid TenantId, Guid ConversationId) : IntegrationEventBase;

/// <summary>
/// The takeover SLA ran out (WA-BE-15): the supervisor is told, and the agent the lead was
/// redispatched to — if anyone could be found — learns it is now theirs.
/// </summary>
public sealed record WhatsAppTakeoverBreachedEvent(
    Guid TenantId,
    Guid ConversationId,
    Guid? BreachedAgentId,
    Guid? NewAgentId,
    Guid? SupervisorId,
    int WaitedMinutes,
    DateTimeOffset BreachedAt) : IntegrationEventBase;

/// <summary>A message of ours failed after the provider accepted it (WA-BE-08): its sender is told.</summary>
public sealed record WhatsAppMessageFailedEvent(Guid TenantId, Guid MessageId) : IntegrationEventBase;

/// <summary>
/// A customer's message is for the bot (WA-BE-11). Its consumer runs the turn out of the webhook's
/// way — the delivery is acknowledged first, the model asked afterwards.
/// </summary>
public sealed record WhatsAppBotTurnRequestedEvent(Guid TenantId, Guid ConversationId, Guid InboundMessageId) : IntegrationEventBase;
