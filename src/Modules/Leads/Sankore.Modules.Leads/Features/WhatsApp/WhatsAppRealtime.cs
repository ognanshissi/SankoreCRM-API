namespace Sankore.Modules.Leads.Features.WhatsApp;

using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel.Realtime;

/// <summary>
/// Tells the people working a lead that its WhatsApp thread moved (WA-FE-13).
///
/// <para>
/// <b>A nudge, never a payload</b> — the rule of <see cref="IRealtimeNotifier"/>: the frame carries
/// the lead, the conversation and the message ids, and the browser then reads the thread under its
/// own permissions and perimeter. A customer's words never travel in a websocket frame.
/// </para>
///
/// <para>
/// <b>Best effort, after the commit.</b> A browser that is not connected misses the nudge and
/// finds the message when it next loads the thread — which it also does on reconnecting, so a
/// lost push costs a delay and never a message.
/// </para>
/// </summary>
public static class WhatsAppRealtime
{
    /// <summary>A customer's message was recorded.</summary>
    public const string MessageReceived = "whatsapp.message.received";

    /// <summary>One of our messages changed status — sent, delivered, read, failed.</summary>
    public const string MessageStatus = "whatsapp.message.status";

    /// <summary>Who holds the conversation changed — taken over, reassigned, closed.</summary>
    public const string ConversationUpdated = "whatsapp.conversation.updated";

    public static async Task PushAsync(
        IRealtimeNotifier notifier,
        ILogger logger,
        Guid tenantId,
        IEnumerable<Guid?> recipients,
        string eventName,
        Guid? leadId,
        Guid conversationId,
        Guid messageId,
        CancellationToken ct)
    {
        var payload = new { leadId, conversationId, messageId };

        foreach (var userId in recipients.Where(r => r is { } id && id != Guid.Empty).Select(r => r!.Value).Distinct())
        {
            try
            {
                await notifier.PushToUserAsync(tenantId, userId, eventName, payload, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never fails the work it reports: the message is recorded either way.
                logger.LogDebug("WhatsApp push {Event} not delivered ({Reason}).", eventName, ex.GetType().Name);
            }
        }
    }
}
