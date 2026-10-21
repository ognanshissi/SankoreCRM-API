namespace Sankore.Modules.Leads.Features.WhatsApp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Hands a conversation to a person (WA-BE-14) — from a customer's message, from a bot rule, from
/// the bot's own decision or from the window sweep. One body, so the four paths name the same
/// agent, start the same SLA and tell the customer the same thing.
///
/// <para>
/// <b>Never saves.</b> Every caller is in the middle of recording what justified the escalation,
/// and commits both together; the escalation's event goes to the same outbox.
/// </para>
/// </summary>
internal sealed class WhatsAppEscalation(
    LeadsDbContext db,
    ICustomersModule customers,
    ILeadSettings settings,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    ILogger<WhatsAppEscalation> logger)
{
    /// <summary>
    /// The agent expected is the one working the lead — its assignee, else its owner — or the
    /// client's advisor; when there is nobody, the escalation's consumer asks M13's dispatching.
    /// The SLA starts now.
    /// </summary>
    /// <param name="profile">
    /// The bot profile that was answering, if any: outside its agents' hours its own message
    /// replaces the tenant's waiting message.
    /// </param>
    /// <param name="tellCustomer">
    /// False when the customer did not just write — the window sweep: an unsolicited « un
    /// conseiller vous répond » to somebody who said nothing is noise.
    /// </param>
    public async Task EscalateAsync(
        Guid tenantId,
        WhatsAppConversation conversation,
        Lead? lead,
        string reason,
        string? summary,
        DateTimeOffset now,
        WhatsAppBotProfile? profile,
        bool tellCustomer,
        CancellationToken ct)
    {
        Guid? target = lead?.CurrentAssignedId ?? lead?.OwnerId;

        if (target is null && conversation.ClientId is { } clientId)
        {
            try
            {
                target = (await customers.GetClientSummaryAsync(tenantId, clientId, ct))?.AdvisorUserId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("WhatsApp: the client's advisor could not be read ({Reason}).", ex.GetType().Name);
            }
        }

        var minutes = await settings.GetIntAsync(tenantId, LeadSettingKeys.WhatsAppTakeoverSlaMinutes, ct);

        conversation.Escalate(reason, summary, target, minutes > 0 ? TimeSpan.FromMinutes(minutes) : null, now);

        // WA-BE-22: the history the metrics read. A handover still open is ended first — it can
        // only be a stale one, since a conversation already waiting is never escalated again.
        await WhatsAppHandovers.EndAsync(db, conversation.Id, WhatsAppHandoverEnd.Closed, null, now, ct);
        db.WhatsAppHandovers.Add(WhatsAppHandover.Open(
            tenantId, conversation.Id, conversation.LeadId, reason,
            fromBot: reason.StartsWith("BOT_", StringComparison.Ordinal), conversation.TakeoverDeadline, now));

        if (tellCustomer)
        {
            var text = profile is not null && !profile.IsWithinAgentHours(now) && !string.IsNullOrWhiteSpace(profile.OutOfHoursMessage)
                ? profile.OutOfHoursMessage
                : await settings.GetStringAsync(tenantId, LeadSettingKeys.WhatsAppWaitingMessage, ct);

            if (!string.IsNullOrWhiteSpace(text))
                await QueueAutomaticAsync(tenantId, conversation, WhatsAppAuthor.System, text, now, ct);
        }

        await publisher.PublishAsync(new WhatsAppConversationEscalatedEvent(tenantId, conversation.Id), ct);
    }

    /// <summary>
    /// A message the platform sends on its own — a rule's (<see cref="WhatsAppAuthor.System"/>) or
    /// the bot's — through the same outbox as an agent's.
    /// </summary>
    public async Task<WhatsAppMessage> QueueAutomaticAsync(
        Guid tenantId, WhatsAppConversation conversation, WhatsAppAuthor author, string text, DateTimeOffset now, CancellationToken ct)
    {
        var automatic = WhatsAppMessage.OutboundText(
            tenantId, conversation.Id, author, authorUserId: null, text, now.AddMilliseconds(1));

        db.WhatsAppMessages.Add(automatic);
        conversation.RecordOutbound(now);

        await publisher.PublishAsync(new WhatsAppMessageQueuedEvent(tenantId, automatic.Id), ct);
        return automatic;
    }
}
