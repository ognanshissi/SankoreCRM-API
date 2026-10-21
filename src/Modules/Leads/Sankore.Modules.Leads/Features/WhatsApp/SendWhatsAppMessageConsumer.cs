namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel.Realtime;

/// <summary>
/// Hands a queued WhatsApp message to the provider — a template (WA-BE-06) or free text
/// (WA-BE-16, the agent's reply and the platform's automatic messages).
///
/// <para>
/// <b>At most once, deliberately.</b> The message is moved to « en cours d'envoi » and SAVED
/// before the provider is called. A redelivered event — MassTransit delivers at least once — or a
/// consumer that crashed mid-call then finds it in that state and does nothing: whether the
/// provider accepted it cannot be known from here, and a customer receiving the same template
/// twice is exactly what the 24-hour rule protects against. The retries that ARE safe — a throttle,
/// the provider down before accepting — happen inside the adapter, which knows which ones those are.
/// </para>
///
/// <para>
/// A failure is recorded on the message with the translated code, and the agent who sent it is
/// nudged so their thread turns red without a reload.
/// </para>
/// </summary>
public sealed class SendWhatsAppMessageConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<SendWhatsAppMessageConsumer> logger)
    : IConsumer<WhatsAppMessageQueuedEvent>
{
    public async Task Consume(ConsumeContext<WhatsAppMessageQueuedEvent> context)
    {
        var (tenantId, messageId) = (context.Message.TenantId, context.Message.MessageId);
        if (tenantId == Guid.Empty || messageId == Guid.Empty) return;

        // The ambient scope first: ITenantContext is built from it, and a consumer has no request.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        await SendAsync(
            sp.GetRequiredService<LeadsDbContext>(),
            sp.GetRequiredService<IIntegrationModule>(),
            sp.GetRequiredService<IRealtimeNotifier>(),
            sp.GetRequiredService<TimeProvider>(),
            logger,
            tenantId,
            messageId,
            context.CancellationToken);
    }

    internal static async Task SendAsync(
        LeadsDbContext db,
        IIntegrationModule integration,
        IRealtimeNotifier realtime,
        TimeProvider clock,
        ILogger logger,
        Guid tenantId,
        Guid messageId,
        CancellationToken ct)
    {
        var message = await db.WhatsAppMessages.AsTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null)
        {
            logger.LogWarning("WhatsApp message {MessageId} of tenant {TenantId} not found; nothing sent.", messageId, tenantId);
            return;
        }

        var conversation = await db.WhatsAppConversations.FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);

        if (conversation is null)
        {
            message.MarkFailed(WhatsAppErrors.SendFailed, "The conversation no longer exists.", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!message.TryBeginSending())
        {
            logger.LogInformation(
                "WhatsApp message {MessageId} is {Status}; a redelivered event sends nothing.", messageId, message.Status);
            return;
        }

        // Saved BEFORE the call: this row is what stops a second consumer from sending it again.
        await db.SaveChangesAsync(ct);

        IntegrationResult<string> sent;

        try
        {
            if (message.Kind == Domain.WhatsAppMessageKind.Template)
            {
                IReadOnlyList<string> values;

                try
                {
                    values = JsonSerializer.Deserialize<List<string>>(message.TemplateParameters ?? "[]") ?? [];
                }
                catch (JsonException)
                {
                    message.MarkFailed(WhatsAppErrors.SendFailed, "The template's values could not be read.", clock.GetUtcNow());
                    await db.SaveChangesAsync(ct);
                    return;
                }

                sent = await integration.Messaging.SendTemplateAsync(
                    tenantId,
                    conversation.ConnectionId,
                    new MessagingTemplateSend(conversation.ContactPhone, message.TemplateName!, message.TemplateLanguage!, values),
                    ct);
            }
            else
            {
                // Free text (WA-BE-16): the provider refuses it outside the window with
                // MESSAGING_OUTSIDE_WINDOW, which is recorded on the message like any refusal.
                sent = await integration.Messaging.SendTextAsync(
                    tenantId, conversation.ConnectionId, conversation.ContactPhone, message.Body ?? string.Empty, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The gateway does not throw by contract; if it does, the message is uncertain rather
            // than resent — the same reasoning as a crash between the two saves.
            logger.LogError(ex, "WhatsApp message {MessageId}: the send threw; recorded as uncertain.", messageId);

            message.MarkFailed(WhatsAppErrors.SendUncertain, null, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await NudgeAsync(realtime, logger, tenantId, conversation.LeadId, conversation.Id, message, ct);
            return;
        }

        if (sent.IsSuccess)
        {
            message.MarkSent(sent.Value, clock.GetUtcNow());
        }
        else
        {
            logger.LogWarning(
                "WhatsApp message {MessageId} of tenant {TenantId} refused: {Code}.", messageId, tenantId, sent.Code);

            message.MarkFailed(sent.Code ?? WhatsAppErrors.SendFailed, sent.Detail, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        await NudgeAsync(realtime, logger, tenantId, conversation.LeadId, conversation.Id, message, ct);
    }

    private static Task NudgeAsync(
        IRealtimeNotifier realtime,
        ILogger logger,
        Guid tenantId,
        Guid? leadId,
        Guid conversationId,
        Domain.WhatsAppMessage message,
        CancellationToken ct)
        => WhatsAppRealtime.PushAsync(
            realtime, logger, tenantId, [message.AuthorUserId], WhatsAppRealtime.MessageStatus,
            leadId, conversationId, message.Id, ct);
}
