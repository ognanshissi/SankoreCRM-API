namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Infrastructure.Behaviors;

/// <summary>
/// The WhatsApp acts the audit trail must show and that no command carries (WA-BE-20).
///
/// <para>
/// Sends, takeovers, reassignments, closures and agents' consent entries are commands, audited by
/// <c>AuditBehavior</c> already. Three acts are not: a supervisor READING a conversation (a query),
/// and the two consent changes a customer's own message makes — a STOP and a first message — which
/// arrive through the webhook under the SYSTEM identity and are deliberately not commands (their
/// payload is a customer's words). They are written here, with identifiers only.
/// </para>
///
/// <para>
/// <b>Best effort.</b> An audit row that cannot be written is logged and the read or the delivery
/// goes on: losing a customer's STOP because the audit table hiccupped would be the worse failure.
/// </para>
/// </summary>
internal sealed class WhatsAppAudit(IAuditWriter writer, TimeProvider clock, ILogger<WhatsAppAudit> logger)
{
    public const string ConversationConsulted = "WhatsAppConversationConsulted";
    public const string OptOutReceived = "WhatsAppOptOutReceived";
    public const string ConsentFromInboundMessage = "WhatsAppConsentFromInboundMessage";

    public Task ConsultedAsync(Guid tenantId, Guid userId, Guid conversationId, Guid? leadId, CancellationToken ct) =>
        WriteAsync(tenantId, userId, ConversationConsulted, conversationId, new { conversationId, leadId }, ct);

    public Task SystemAsync(Guid tenantId, string action, Guid conversationId, Guid? leadId, CancellationToken ct) =>
        WriteAsync(tenantId, Guid.Empty, action, conversationId, new { conversationId, leadId }, ct);

    private async Task WriteAsync(Guid tenantId, Guid userId, string action, Guid conversationId, object payload, CancellationToken ct)
    {
        try
        {
            await writer.WriteAsync(new AuditEntry(
                Timestamp: clock.GetUtcNow(),
                UserId: userId,
                TenantId: tenantId,
                Action: action,
                PayloadJson: JsonSerializer.Serialize(payload),
                Outcome: "SUCCESS",
                ErrorDetail: null,
                ResourceType: "WhatsAppConversation",
                ResourceId: conversationId.ToString()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "WhatsApp audit entry {Action} for conversation {ConversationId} could not be written.", action, conversationId);
        }
    }
}
